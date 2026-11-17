-- =============================================================================
--  DCS-UCS (Universal Control Sync) - Cockpit Export
--  Install: Saved Games\DCS\Scripts\UCS\UCS-Export.lua
--  Loaded from Saved Games\DCS\Scripts\Export.lua with:
--    pcall(function() local ucsLfs=require('lfs'); dofile(ucsLfs.writedir()..[[Scripts\UCS\UCS-Export.lua]]); end) -- DCS-UCS
--
--  Chains LuaExportStart / LuaExportAfterNextFrame / LuaExportStop so other
--  mods (SRS, Tacview, DCS-BIOS, ...) keep working.
--
--  Local UDP protocol (plain text, one message per datagram):
--    Export -> Client (127.0.0.1:5051)
--      HELLO|<version>
--      SELF|<aircraft name>|<active 0/1>
--      ARG|<arg>=<val>;<arg>=<val>;...    (local changes, broadcast to the crew)
--      SNAP|<arg>=<val>;...               (full state answer to SNAPSHOT)
--      BYE
--    Client -> Export (listen 127.0.0.1:5052)
--      PROFILE_BEGIN|<aircraft>|<parts>
--      PROFILE_PART|<aircraft>|<index>|<arg>:<dev>:<cmd>;<arg>:<dev>:<cmd>;...
--      PROFILE_END|<aircraft>
--      STANDBY
--      SET|<arg>=<val>;<arg>=<val>;...
--      SNAPSHOT            (asks Export to resend every watched argument)
--
--  Flight controls (0.1.2+). Stick / pedals / collective are not clickable: the mod
--  animates them from its own flight model, so they use a separate channel.
--    Client -> Export
--      FLIGHT|<arg>:<axis>:<min>:<max>;...       flight-control animation args
--      FMODE|<send>|<inject 0/1/2>|<diag>|<axis>=<cmd>:<sign>,...;...|<hook seat>
--            inject 1 = automatic: only while this seat does NOT have the controls
--            inject 2 = forced (tests only, also with the controls)
--      FSET|<arg>=<val>;...                      flight args of whoever is flying
--      TRIM|<release>|<releaseEnd>|<set>|<reset> the mod's trim commands (hand-over)
--      EXTDEF|<args>:<cmds>:<arg>><cmd>,<cmd>/<arg>><cmd>|...   external states per mod script
--            (args it animates, safe key commands it listens to, commands known to change each arg)
--      EXTSET|<arg>=<val>;...                    external state of the crew
--    Export -> Client
--      FLT|<arg>=<val>;...                       local flight args (~30 Hz, on change)
--      DIAG|<text>                               diagnostic messages for the client log
--      CTRL|<master 1/0/?>|<seat 0-based/?>|<mirroring 0/1>   who has the controls (on change + every 2 s)
--      EXT|<arg>=<val>;...                       settled change of an external state (broadcast)
--      EXTFULL|<arg>=<val>;...                   all settled external states every 5 s (late joiners)
--
--  "inject" (experimental) feeds the received stick position into this seat as joystick input
--  (LoSetCommand) so the mod animates the stick like the pilot's. Automatic: it runs only while
--  DCS says this seat is NOT in control of the flight model (LoGetFMData().master == false in
--  multicrew); when that flag is unavailable, only in a seat other than the first one.
--  "diag" writes Saved Games\DCS\Logs\UCS\UCS-diag-<time>.csv (every frame) and reports
--  flickering cockpit arguments (two sources fighting over the same animation).
-- =============================================================================

local UCS = {
    version       = "0.1.8-alpha",
    host          = "127.0.0.1",
    sendPort      = 5051,
    listenPort    = 5052,
    pollInterval  = 0.05,   -- 20 Hz argument scan
    selfInterval  = 1.0,    -- aircraft name report
    threshold     = 0.01,   -- minimum change to be sent
    lockTime      = 0.3,    -- loop-prevention lock after a remote command
    maxPayload    = 3500,   -- bytes per datagram (LuaSocket caps receives at 8 KB)
}

UCS.socket       = nil
UCS.udp          = nil
UCS.active       = false
UCS.selfName     = ""
UCS.profile      = nil       -- { aircraft = "...", args = { [arg] = {dev=, cmd=} } }
UCS.pending      = nil       -- profile being received
UCS.lastValues   = {}
UCS.ignoreUntil  = {}
UCS.nextPoll     = 0
UCS.nextSelf     = 0
UCS.forceAll     = false

-- Flight-control channel
UCS.flight       = {}        -- list of { arg=, axis=, min=, max= }
UCS.fsend        = false
UCS.finject      = false     -- mirror enabled by the client (automatic)
UCS.fforce       = false     -- tests only: inject even with the controls
UCS.hookSeat     = nil       -- seat reported by the GameGUI hook (1-based), fallback
UCS.master       = nil       -- true/false: this seat controls the flight model (nil = unknown)
UCS.seat         = nil       -- LoGetPlayerSeat() (0-based, nil = unknown)
UCS.mirroring    = false
UCS.trim         = nil       -- { release=, releaseEnd=, set=, reset= } (command ids) or nil
UCS.handoff      = nil       -- trim hand-over in progress { stage=, due= }
UCS.ext          = { groups = {}, byArg = {}, text = "" }   -- external states (see extFrame)
UCS.nextCtrlSend = 0
UCS.fdiag        = false
UCS.fcmds        = {}        -- axis -> { cmd=, sign= }
UCS.fremote      = {}        -- arg -> latest received value
UCS.fremoteAt    = 0
UCS.fsent        = {}        -- arg -> last value sent
UCS.nextFlt      = 0
UCS.fltInterval  = 1 / 30
UCS.axisPause    = {}        -- axis -> paused until (local input detected)
UCS.axisPauses   = {}        -- axis -> pauses in a row without ever following the input
UCS.axisOff      = {}        -- axis -> true when disabled (does not follow the injected input)
UCS.injectOff    = {}        -- axis -> time the local input started to disagree
UCS.injected     = {}        -- axis -> last injected -1..1 value
UCS.cal          = {}        -- axis -> { idx = candidate in use, ok = calibrated, since = }
UCS.fcmdsText    = ""
UCS.diag         = nil

local dcsLog = log -- DCS global logging module (may be nil in old versions)
local function log(msg)
    if dcsLog and dcsLog.write then
        pcall(dcsLog.write, "UCS", dcsLog.INFO, tostring(msg))
    end
end

local function split(str, sep)
    local out = {}
    if str == nil or str == "" then return out end
    local pos = 1
    while true do
        local s, e = string.find(str, sep, pos, true)
        if not s then
            table.insert(out, string.sub(str, pos))
            break
        end
        table.insert(out, string.sub(str, pos, s - 1))
        pos = e + 1
    end
    return out
end

local function send(msg)
    if UCS.udp then
        pcall(function() UCS.udp:sendto(msg, UCS.host, UCS.sendPort) end)
    end
end

-- Sends "PREFIX|a;b;c" splitting the entries over several datagrams if needed.
local function sendChunked(prefix, entries)
    local buf = {}
    local size = 0
    for i = 1, #entries do
        local e = entries[i]
        if size + #e + 1 > UCS.maxPayload and #buf > 0 then
            send(prefix .. table.concat(buf, ";"))
            buf = {}
            size = 0
        end
        table.insert(buf, e)
        size = size + #e + 1
    end
    if #buf > 0 then
        send(prefix .. table.concat(buf, ";"))
    end
end

local function now()
    return os.clock()
end

-- Wall clock (os.clock is process CPU time). LuaSocket is loaded in LuaExportStart.
local function wall()
    if UCS.socket and UCS.socket.gettime then return UCS.socket.gettime() end
    return os.clock()
end

local function getSelfName()
    local ok, data = pcall(LoGetSelfData)
    if ok and type(data) == "table" and data.Name then
        return tostring(data.Name)
    end
    return ""
end

-- Forget everything learnt about the current aircraft's flight controls. Called when leaving an
-- aircraft (standby) and when a different flight-control list arrives, so that nothing (calibration,
-- mirror state, a pending trim hand-over) leaks from one aircraft or seat to the next.
local function resetFlightState()
    UCS.fremote = {}
    UCS.fremoteAt = 0
    UCS.fsent = {}
    UCS.injected = {}
    UCS.injectOff = {}
    UCS.cal = {}
    UCS.axisPause, UCS.axisPauses, UCS.axisOff = {}, {}, {}
    UCS.mirroring = false          -- set silently: no trim reset / hand-over for a change of aircraft
    UCS.handoff = nil
    UCS.master, UCS.seat = nil, nil
    if UCS.extReset then UCS.extReset() end
end

local function goStandby(reason)
    if UCS.active then
        log("Standby: " .. tostring(reason))
    end
    UCS.active = false
    UCS.lastValues = {}
    UCS.ignoreUntil = {}
    resetFlightState()
end

local function activateIfPossible()
    local p = UCS.profile
    if not p then
        goStandby("no profile")
        return
    end
    -- If LoGetSelfData is blocked by the server (export restrictions) the name
    -- is empty: trust the Client, which also knows the type from the GameGUI hook.
    if UCS.selfName == "" or string.lower(UCS.selfName) == string.lower(p.aircraft) then
        if not UCS.active then
            log("Active: " .. p.aircraft .. " (" .. tostring(p.count) .. " args)")
        end
        UCS.active = true
        UCS.lastValues = {}
        UCS.forceAll = false
    else
        goStandby("aircraft mismatch " .. UCS.selfName .. " vs " .. p.aircraft)
    end
end

local function reportSelf()
    send("SELF|" .. UCS.selfName .. "|" .. (UCS.active and "1" or "0"))
end

-- -----------------------------------------------------------------------------
-- Incoming commands
-- -----------------------------------------------------------------------------
local function applyRemote(payload)
    if not UCS.active or not UCS.profile then return end
    local cockpit = GetDevice(0)
    local t = now()
    local entries = split(payload, ";")
    for i = 1, #entries do
        local kv = split(entries[i], "=")
        local arg = tonumber(kv[1])
        local val = tonumber(kv[2])
        if arg and val then
            local def = UCS.profile.args[arg]
            -- Already in the requested position: do nothing. Some mods implement switches
            -- as toggles (each click flips the state, ignoring the value), so "clicking"
            -- a switch that already matches would invert it (e.g. UH-60L doors).
            local same = false
            if def and cockpit then
                local okc, cur = pcall(cockpit.get_argument_value, cockpit, arg)
                same = okc and type(cur) == "number" and math.abs(cur - val) < UCS.threshold
            end
            if def and same then
                UCS.lastValues[arg] = val
            elseif def then
                -- Loop prevention: lock this argument and remember the value
                UCS.ignoreUntil[arg] = t + UCS.lockTime
                UCS.lastValues[arg] = val
                local ok, err = pcall(function()
                    local dev = GetDevice(def.dev)
                    if dev then
                        dev:performClickableAction(def.cmd, val)
                    end
                end)
                if not ok then
                    log("performClickableAction failed arg " .. arg .. ": " .. tostring(err))
                    -- Fallback: at least move the animation so both cockpits match visually
                    if cockpit then pcall(function() cockpit:set_argument_value(arg, val) end) end
                end
            end
        end
    end
end

local diagOpen, diagClose -- defined in the Diagnostics section

local function handleMessage(msg)
    local parts = split(msg, "|")
    local kind = parts[1]
    if kind == "PROFILE_BEGIN" then
        UCS.pending = { aircraft = parts[2] or "", parts = tonumber(parts[3]) or 0, received = {}, args = {}, count = 0 }
    elseif kind == "PROFILE_PART" then
        local p = UCS.pending
        if p and parts[2] == p.aircraft then
            local idx = tonumber(parts[3]) or 0
            if not p.received[idx] then
                p.received[idx] = true
                local entries = split(parts[4] or "", ";")
                for i = 1, #entries do
                    local f = split(entries[i], ":")
                    local arg, dev, cmd = tonumber(f[1]), tonumber(f[2]), tonumber(f[3])
                    if arg and dev and cmd then
                        if not p.args[arg] then p.count = p.count + 1 end
                        p.args[arg] = { dev = dev, cmd = cmd }
                    end
                end
            end
        end
    elseif kind == "PROFILE_END" then
        local p = UCS.pending
        if p and parts[2] == p.aircraft then
            local got = 0
            for _ in pairs(p.received) do got = got + 1 end
            if got >= p.parts then
                UCS.profile = { aircraft = p.aircraft, args = p.args, count = p.count }
                UCS.pending = nil
                activateIfPossible()
            else
                log("Incomplete profile (" .. got .. "/" .. p.parts .. "), waiting for resend")
                UCS.pending = nil
                UCS.profile = nil
                goStandby("incomplete profile")
            end
            reportSelf()
        end
    elseif kind == "STANDBY" then
        UCS.profile = nil
        goStandby("requested by client")
        reportSelf()
    elseif kind == "SET" then
        applyRemote(parts[2] or "")
    elseif kind == "SNAPSHOT" then
        UCS.forceAll = true
    elseif kind == "FLIGHT" then
        local list = {}
        local entries = split(parts[2] or "", ";")
        for i = 1, #entries do
            local f = split(entries[i], ":")
            local arg, mn, mx = tonumber(f[1]), tonumber(f[3]), tonumber(f[4])
            if arg and f[2] and mn and mx and mn ~= mx then
                table.insert(list, { arg = arg, axis = f[2], min = mn, max = mx })
            end
        end
        local text = parts[2] or ""
        if text ~= UCS.flightText then
            UCS.flightText = text
            resetFlightState()
            -- new columns for the new aircraft
            if UCS.diag then diagClose(); UCS.flight = list; diagOpen() end
        end
        UCS.flight = list
        UCS.fsent = {}
        log("Flight-control args: " .. #list)
    elseif kind == "FMODE" then
        UCS.fsend = parts[2] == "1"
        local inject = parts[3] == "1" or parts[3] == "2"
        UCS.fforce = parts[3] == "2"
        UCS.hookSeat = tonumber(parts[6] or "")
        local diag = parts[4] == "1"
        -- axis=cmd:sign,cmd:sign;...  (candidates in order, see calibrate())
        local cmds = {}
        local entries = split(parts[5] or "", ";")
        for i = 1, #entries do
            local kv = split(entries[i], "=")
            local list = {}
            local cands = split(kv[2] or "", ",")
            for j = 1, #cands do
                local cs = split(cands[j], ":")
                local cmd, sign = tonumber(cs[1]), tonumber(cs[2]) or 1
                if cmd then table.insert(list, { cmd = cmd, sign = (sign < 0) and -1 or 1 }) end
            end
            if kv[1] and #list > 0 then cmds[kv[1]] = list end
        end
        local changed = table.concat(entries, ";") ~= UCS.fcmdsText
        UCS.fcmdsText = table.concat(entries, ";")
        UCS.fcmds = cmds
        if changed then UCS.cal = {} end
        if inject ~= UCS.finject then
            log("Flight input mirror (experimental): " .. (inject and "ON" or "OFF"))
            UCS.injected = {}
            UCS.injectOff = {}
            UCS.axisPause, UCS.axisPauses, UCS.axisOff = {}, {}, {}
            UCS.cal = {}
        end
        UCS.finject = inject
        if diag and not UCS.diag then diagOpen() elseif not diag and UCS.diag then diagClose() end
        UCS.fdiag = diag
    elseif kind == "TRIM" then
        local r, re, st, rs = tonumber(parts[2] or ""), tonumber(parts[3] or ""), tonumber(parts[4] or ""), tonumber(parts[5] or "")
        if rs and ((r and re) or st) then
            UCS.trim = { release = r, releaseEnd = re, set = st, reset = rs }
        else
            UCS.trim = nil
        end
    elseif kind == "EXTDEF" then
        local text = table.concat(parts, "|", 2)
        if text ~= UCS.ext.text then
            local groups, byArg = {}, {}
            for i = 2, #parts do
                local ac = split(parts[i], ":")
                local g = { args = {}, cmds = {}, prefer = {}, hasPrefer = false }
                for _, a in ipairs(split(ac[1] or "", ",")) do local n = tonumber(a); if n then table.insert(g.args, n) end end
                for _, c in ipairs(split(ac[2] or "", ",")) do local n = tonumber(c); if n then table.insert(g.cmds, n) end end
                for _, pr in ipairs(split(ac[3] or "", "/")) do
                    local kv = split(pr, ">")
                    local a = tonumber(kv[1])
                    if a then
                        g.prefer[a] = {}
                        for _, c in ipairs(split(kv[2] or "", ",")) do local n = tonumber(c); if n then table.insert(g.prefer[a], n) end end
                        g.hasPrefer = true
                    end
                end
                if #g.args > 0 and #g.cmds > 0 then
                    table.insert(groups, g)
                    for _, a in ipairs(g.args) do byArg[a] = byArg[a] or g end
                end
            end
            UCS.ext.groups, UCS.ext.byArg, UCS.ext.text = groups, byArg, text
            if UCS.extReset then UCS.extReset() end
            log("External states: " .. #groups .. " group(s)")
        end
    elseif kind == "EXTSET" then
        local t = wall()
        for _, e in ipairs(split(parts[2] or "", ";")) do
            local kv = split(e, "=")
            local arg, val = tonumber(kv[1]), tonumber(kv[2])
            if arg and val and UCS.ext.byArg[arg] then
                if UCS.ext.remote[arg] == nil or math.abs(UCS.ext.remote[arg] - val) > 0.02 then UCS.ext.remoteAt[arg] = t end
                UCS.ext.remote[arg] = val
                UCS.ext.tries[arg] = 0       -- a new state from the crew: allow new tries
            end
        end
    elseif kind == "FSET" then
        local entries = split(parts[2] or "", ";")
        for i = 1, #entries do
            local kv = split(entries[i], "=")
            local arg, val = tonumber(kv[1]), tonumber(kv[2])
            if arg and val then UCS.fremote[arg] = val end
        end
        UCS.fremoteAt = wall()
    end
end

local function receiveAll()
    if not UCS.udp then return end
    for _ = 1, 64 do
        local data = UCS.udp:receive()
        if not data then break end
        local ok, err = pcall(handleMessage, data)
        if not ok then log("message error: " .. tostring(err)) end
    end
end

-- -----------------------------------------------------------------------------
-- Argument scanning
-- -----------------------------------------------------------------------------
local function scanArguments()
    if not UCS.active or not UCS.profile then return end
    local cockpit = GetDevice(0)
    if not cockpit then return end
    local t = now()
    local changes = {}
    local snapshot = {}
    local force = UCS.forceAll
    UCS.forceAll = false
    for arg, _ in pairs(UCS.profile.args) do
        local ok, val = pcall(cockpit.get_argument_value, cockpit, arg)
        if ok and type(val) == "number" then
            local entry = arg .. "=" .. string.format("%.3f", val)
            if force then
                -- Full state for the Client (stored locally, not broadcast)
                table.insert(snapshot, entry)
            end
            local locked = UCS.ignoreUntil[arg] and t < UCS.ignoreUntil[arg]
            if locked then
                -- Track silently while a remote command is settling
                UCS.lastValues[arg] = val
            else
                UCS.ignoreUntil[arg] = nil
                local last = UCS.lastValues[arg]
                if last == nil then
                    UCS.lastValues[arg] = val
                elseif math.abs(val - last) > UCS.threshold then
                    UCS.lastValues[arg] = val
                    table.insert(changes, entry)
                end
            end
        end
    end
    if #snapshot > 0 then
        sendChunked("SNAP|", snapshot)
    end
    if #changes > 0 then
        sendChunked("ARG|", changes)
    end
end


-- -----------------------------------------------------------------------------
-- Flight controls
-- -----------------------------------------------------------------------------
local function readArg(cockpit, arg)
    local ok, val = pcall(cockpit.get_argument_value, cockpit, arg)
    if ok and type(val) == "number" then return val end
    return nil
end

-- Argument value (gauge output range) -> -1..1 joystick axis
local function toAxis(f, v)
    local n = 2 * (v - f.min) / (f.max - f.min) - 1
    if n > 1 then n = 1 elseif n < -1 then n = -1 end
    return n
end

-- -----------------------------------------------------------------------------
-- Who has the controls
-- -----------------------------------------------------------------------------
local function readControl()
    local master, seat = nil, nil
    for _, fn in ipairs({ "LoGetHelicopterFMData", "LoGetFMData" }) do
        local f = _G[fn]
        if master == nil and type(f) == "function" then
            local ok, fm = pcall(f)
            if ok and type(fm) == "table" and type(fm.master) == "boolean" then master = fm.master end
        end
    end
    if type(LoGetPlayerSeat) == "function" then
        local ok, n = pcall(LoGetPlayerSeat)
        if ok and type(n) == "number" then seat = n end
    end
    return master, seat
end

-- Value of a cockpit parameter (nil when the mod does not have it)
local function cockpitParam(name)
    if type(list_cockpit_params) ~= "function" then return nil end
    local ok, list = pcall(list_cockpit_params)
    if not ok or type(list) ~= "string" then return nil end
    return tonumber(string.match(list, "%f[%w_]" .. name .. ":%s*(%-?[%d%.]+)"))
end

-- -----------------------------------------------------------------------------
-- Trim hand-over (verified on the OH-6A, 2026-11-17)
--   losing the controls : trim reset   -> the mirrored stick shows exactly the pilot's
--   taking the controls : trim release (+ release end, or trim set) while the input still holds the
--                         pilot's stick, then input back to centre -> this seat's trim is the position
--                         the aircraft was being flown with, so a spring joystick at centre continues
--                         seamlessly. Skipped for force-feedback / springless sticks (TRIMMING_METHOD 1).
-- -----------------------------------------------------------------------------
local function trimSupported()
    if not UCS.trim then return false end
    local method = cockpitParam("TRIMMING_METHOD")
    return method == nil or method == 0
end

-- Axes the trim holds (yaw only when the mod's pedal trim is on)
local function trimAxes()
    local axes = { "pitch", "roll" }
    if cockpitParam("PEDAL_TRIM") == 1 then table.insert(axes, "yaw") end
    return axes
end

local function startHandoff(t)
    if not trimSupported() then return end
    -- every trimmed axis must be calibrated, or so close to centre that its direction does not matter
    local h = { stage = 1, due = t, axes = {} }
    for _, axis in ipairs(trimAxes()) do
        local cal, cands, inj = UCS.cal[axis], UCS.fcmds[axis], UCS.injected[axis]
        local c = cands and cal and cands[cal.idx]
        if cal and cal.ok and c and inj then
            h.axes[axis] = { cmd = c.cmd, sign = c.sign, value = inj * c.sign }   -- injected, in axis units
        elseif c and inj and math.abs(inj) <= 0.15 then
            -- not calibrated but close to centre: no known direction, so just ramp its input to 0 in 1 s
            h.axes[axis] = { cmd = c.cmd, sign = c.sign, value = inj * c.sign, ramp = inj * c.sign }
        elseif inj and math.abs(inj) > 0.15 then
            send("DIAG|Trim hand-over skipped: the " .. axis .. " mirror was not calibrated yet")
            return
        end
    end
    -- goal = where the stick is NOW (the crew member's position), read before the trim starts to change
    local cockpit = GetDevice(0)
    for i = 1, #UCS.flight do
        local f = UCS.flight[i]
        local a = h.axes[f.axis]
        if a and not a.f then
            local v = cockpit and readArg(cockpit, f.arg)
            a.f, a.goal = f, v and toAxis(f, v) or a.value
        end
    end
    UCS.handoff = h
end

local function runHandoff(t)
    local h = UCS.handoff
    if not h or t < h.due then return end
    local tr = UCS.trim
    if h.stage == 1 then
        if tr.release and tr.releaseEnd then pcall(LoSetCommand, tr.release, 1) else pcall(LoSetCommand, tr.set, 1) end
        h.stage, h.due = 2, t + 0.15
    elseif h.stage == 2 then
        if tr.release and tr.releaseEnd then pcall(LoSetCommand, tr.releaseEnd, 1) end
        -- compensation starts on this very frame: the mod begins fading the trim in right away
        h.stage, h.due, h.started = 4, t, t
    else
        -- the mod fades the new trim in: lower the input by exactly what the trim already holds, so
        -- input + trim stays on the goal; done when the input is back to centre (max 5 s)
        local cockpit = GetDevice(0)
        local done = true
        for _, a in pairs(h.axes) do
            if a.ramp then
                local want = a.ramp * math.max(0, 1 - (t - h.started) / 1.0)
                if math.abs(want) < 0.01 then want = 0 end
                a.value = want
                pcall(LoSetCommand, a.cmd, want * a.sign)
                if want ~= 0 then done = false end
            elseif a.f and cockpit then
                local v = readArg(cockpit, a.f.arg)
                if v then
                    local trimNow = toAxis(a.f, v) - a.value
                    local want = a.goal - trimNow
                    if want > 1 then want = 1 elseif want < -1 then want = -1 end
                    -- never move away from centre: the input only goes down towards 0
                    if math.abs(want) > math.abs(a.value) or (want > 0) ~= (a.value > 0) then want = a.value * 0.9 end
                    if math.abs(want) < 0.01 or t - h.started > 5 then want = 0 end
                    a.value = want
                    pcall(LoSetCommand, a.cmd, want * a.sign)
                    if want ~= 0 then done = false end
                end
            end
        end
        if done then
            local names = {}
            for axis, _ in pairs(h.axes) do table.insert(names, axis) end
            table.sort(names)
            UCS.handoff = nil
            log("Trim handed over (" .. table.concat(names, ", ") .. ")")
            send("DIAG|Trim handed over: your trim now holds the stick position the crew member was flying with (" ..
                table.concat(names, ", ") .. string.format(", %.1f s)", t - h.started))
        else
            h.due = t   -- every frame
        end
    end
end

-- The mirror runs only in a seat that does not fly the aircraft.
local function mirrorAllowed()
    if not UCS.finject then return false end
    if UCS.fforce then return true end
    if UCS.master ~= nil then return not UCS.master end
    if UCS.seat ~= nil then return UCS.seat > 0 end
    if UCS.hookSeat ~= nil then return UCS.hookSeat > 1 end
    return false
end

local function updateControl(t)
    runHandoff(t)
    -- every frame: the mirror must stop on the very frame this seat takes the controls
    local master, seat = readControl()
    local changed = master ~= UCS.master or seat ~= UCS.seat
    UCS.master, UCS.seat = master, seat
    local mirroring = mirrorAllowed()
    if mirroring ~= UCS.mirroring then
        changed = true
        -- trim hand-over, only on a real change of controls reported by DCS
        if UCS.master ~= nil and UCS.finject then
            if mirroring and UCS.trim and trimSupported() then
                pcall(LoSetCommand, UCS.trim.reset, 1)
                UCS.handoff = nil
                send("DIAG|Trim reset: you do not have the controls, your stick shows the crew member's")
            elseif not mirroring and UCS.master == true then
                startHandoff(t)          -- copies UCS.injected before it is cleared below
            end
        end
        UCS.mirroring = mirroring
        -- start each mirror period clean (pauses, calibration of axes that never followed)
        UCS.injected, UCS.injectOff = {}, {}
        UCS.axisPause, UCS.axisPauses = {}, {}
        log("Flight input mirror " .. (mirroring and "ACTIVE (this seat does not have the controls)" or "inactive"))
    end
    if changed or t >= UCS.nextCtrlSend then
        UCS.nextCtrlSend = t + 2
        send("CTRL|" .. (master == nil and "?" or (master and "1" or "0")) .. "|" ..
            (seat == nil and "?" or tostring(seat)) .. "|" .. (mirroring and "1" or "0"))
    end
end

local function sendFlight(cockpit, t)
    if not UCS.fsend or UCS.mirroring or #UCS.flight == 0 or t < UCS.nextFlt then return end
    UCS.nextFlt = t + UCS.fltInterval
    -- Changes at 30 Hz, plus the full state every 0.4 s: a crew member who joins or loses the
    -- controls gets every axis at once instead of waiting for the pilot to move it (field test
    -- 2026-11-17: axes without a value flickered in the copilot seat).
    local full = t >= (UCS.nextFullFlt or 0)
    if full then UCS.nextFullFlt = t + 0.4 end
    local out = {}
    for i = 1, #UCS.flight do
        local f = UCS.flight[i]
        local v = readArg(cockpit, f.arg)
        if v then
            local last = UCS.fsent[f.arg]
            if full or last == nil or math.abs(v - last) > 0.002 then
                UCS.fsent[f.arg] = v
                table.insert(out, f.arg .. "=" .. string.format("%.4f", v))
            end
        end
    end
    if #out > 0 then send("FLT|" .. table.concat(out, ";")) end
end

-- Feeds the remote stick position into this seat as joystick input.
--
-- Each axis has a list of candidate commands (FMODE "pitch=2001:1,2001:-1"): mods differ in the input
-- they read and in its direction (OH-6A: pitch inverted, collective on the thrust axis 2004 inverted).
-- Calibration (robust to a moving stick): inject with a candidate and wait until the received
-- position has been steady (+-0.05) and clearly deflected (>0.15) for 0.5 s, then compare:
--   animation = received        -> candidate kept
--   animation = -received       -> jump to the same command with the opposite sign
--   anything else               -> next candidate
-- When no candidate works, start over 10 s later (never disabled for good: the pilot may simply
-- have been moving the stick, field test 2026-11-17).
local function calibrate(f, axis, cands, cal, la, target, t)
    if cal.retryAt then
        if t < cal.retryAt then return end
        cal.retryAt, cal.idx, cal.since = nil, 1, nil
    end
    if math.abs(target) < 0.15 then cal.since = nil; return end
    if not cal.since or math.abs(target - (cal.ref or target)) > 0.05 then
        cal.since, cal.ref = t, target
        return
    end
    if t - cal.since < 0.5 then return end
    local c = cands[cal.idx]
    -- levers are not always linear (OH-6A throttle idles at 0.09): a bit more tolerance there
    local tol = (axis == "throttle" or axis == "collective") and 0.2 or 0.12
    if math.abs(la - target) < tol then
        cal.ok = true
        log("Flight input mirror: " .. axis .. " calibrated, command " .. c.cmd .. (c.sign < 0 and " inverted" or ""))
        send("DIAG|Flight input mirror: " .. axis .. " uses command " .. c.cmd .. (c.sign < 0 and " (inverted)" or "") ..
            " (arg " .. f.arg .. ")")
        return
    end
    cal.since = nil
    if math.abs(la + target) < tol then
        for j = 1, #cands do
            if cands[j].cmd == c.cmd and cands[j].sign == -c.sign then cal.idx = j; return end
        end
    end
    cal.idx = cal.idx + 1
    if cal.idx > #cands then
        cal.retryAt = t + 10
        send("DIAG|Flight input mirror: " .. axis .. " - no candidate command moves arg " .. f.arg ..
            string.format(" yet (local %.2f vs received %.2f); retrying in 10 s.", la, target))
    end
end

local function injectFlight(cockpit, t)
    if not UCS.mirroring or #UCS.flight == 0 then return end
    if t - UCS.fremoteAt > 1.5 then return end          -- stream stopped (full state every 0.4 s): leave inputs alone
    for i = 1, #UCS.flight do
        local f = UCS.flight[i]
        local axis = f.axis
        local cands = UCS.fcmds[axis]
        local rv = UCS.fremote[f.arg]
        if cands and #cands > 0 and rv and not UCS.axisOff[axis] and t >= (UCS.axisPause[axis] or 0) then
            local target = toAxis(f, rv)
            local cal = UCS.cal[axis]
            if not cal then cal = { idx = 1 }; UCS.cal[axis] = cal end
            local lv = readArg(cockpit, f.arg)
            local la = lv and toAxis(f, lv)
            local paused = false
            if la and UCS.injected[axis] then
                if not cal.ok then
                    calibrate(f, axis, cands, cal, la, target, t)
                elseif UCS.master == nil and math.abs(la - target) > 0.25 then
                    -- Fallback mode only (DCS does not report who has the controls): the local
                    -- control disagrees for more than 1 s -> pause that axis for 3 s.
                    UCS.injectOff[axis] = UCS.injectOff[axis] or t
                    if t - UCS.injectOff[axis] > 1.0 then
                        paused = true
                        UCS.axisPause[axis] = t + 3
                        UCS.injectOff[axis] = nil
                        UCS.injected[axis] = nil
                        UCS.axisPauses[axis] = (UCS.axisPauses[axis] or 0) + 1
                        log("Flight input mirror: " .. axis .. " paused, local input detected")
                        send("DIAG|Flight input mirror: " .. axis .. " paused 3 s, the local control disagrees (arg " .. f.arg ..
                            string.format(", local %.2f vs received %.2f)", la, target))
                        if UCS.axisPauses[axis] >= 3 then
                            UCS.axisOff[axis] = true
                            send("DIAG|Flight input mirror: " .. axis .. " DISABLED after 3 pauses (the local control keeps moving)")
                        end
                    end
                else
                    UCS.injectOff[axis] = nil
                    UCS.axisPauses[axis] = 0
                end
            end
            local c = cands[cal.idx]
            if c and not paused and not UCS.axisOff[axis] then   -- no candidate while waiting to retry
                local cmdValue = target * c.sign
                pcall(LoSetCommand, c.cmd, cmdValue)
                UCS.injected[axis] = cmdValue
            end
        end
    end
end



-- -----------------------------------------------------------------------------
-- External states (0.1.6): doors, landing light... that the mod changes only from the keyboard.
-- The profile lists, per mod script, the external draw args it animates and the key commands it
-- listens to (EXTDEF). A settled local change is sent (EXT); a crew member whose settled value
-- differs presses the mod's own command, learning which one fixes that argument. Guards: only
-- settled, non-negative values; a try that changes something else is undone; max tries per arg.
-- -----------------------------------------------------------------------------
local function extReset()
    UCS.ext.loc, UCS.ext.sent, UCS.ext.remote, UCS.ext.remoteAt = {}, {}, {}, {}
    UCS.ext.learned, UCS.ext.bad, UCS.ext.tries, UCS.ext.blocked = {}, {}, {}, {}
    UCS.ext.pending, UCS.ext.primed = nil, false
    UCS.ext.ownAt, UCS.ext.appliedAt = {}, {}
    UCS.ext.nextScan, UCS.ext.nextApply, UCS.ext.nextFull = 0, 0, 0
    UCS.ext.startAt = nil
end

local function extRead(arg)
    local ok, v = pcall(LoGetAircraftDrawArgumentValue, arg)
    if ok and type(v) == "number" then return v end
    return nil
end

local function extSettled(e, arg, t, secs)
    local l = e.loc[arg]
    return l and t - l.since >= secs
end

local function extFrame(t)
    local e = UCS.ext
    if #e.groups == 0 or type(LoGetAircraftDrawArgumentValue) ~= "function" then return end
    if t < e.nextScan then return end
    e.nextScan = t + 0.1
    e.startAt = e.startAt or t

    -- 1) local values and how long each has been steady
    for arg, _ in pairs(e.byArg) do
        local v = extRead(arg)
        if v then
            local l = e.loc[arg]
            if not l then e.loc[arg] = { v = v, since = t }
            elseif math.abs(v - l.v) > 0.02 then l.v, l.since = v, t end
        end
    end

    -- 2) send settled changes; the first values after entering the aircraft are only primed (the crew
    --    member already there wins: newcomers get its state through the snapshot)
    local out, full = {}, {}
    for arg, l in pairs(e.loc) do
        if t - l.since >= 0.5 then
            table.insert(full, arg .. "=" .. string.format("%.3f", l.v))
            local s = e.sent[arg]
            if s == nil then
                e.sent[arg] = l.v
            elseif math.abs(l.v - s) > 0.05 then
                e.sent[arg] = l.v
                if e.primed then table.insert(out, arg .. "=" .. string.format("%.3f", l.v)) end
                -- a change made in this seat (not by one of our presses): an older crew state must not undo it
                if not e.pending and t - (e.appliedAt[arg] or -100) > 3 then e.ownAt[arg] = t end
            end
        end
    end
    if not e.primed and t - e.startAt > 3 then e.primed = true end
    if #out > 0 then send("EXT|" .. table.concat(out, ";")) end
    if t >= e.nextFull and #full > 0 then
        e.nextFull = t + 5
        send("EXTFULL|" .. table.concat(full, ";"))   -- kept by the client for late joiners, not broadcast
    end

    -- 3) a try in progress: did it fix the argument?
    local p = e.pending
    if p then
        if t - p.at < 2.5 then return end
        local l, r = e.loc[p.arg], e.remote[p.arg]
        if l and r and math.abs(l.v - r) <= 0.15 then
            e.learned[p.arg] = p.cmd
            send("DIAG|External state: arg " .. p.arg .. " synced with key command " .. p.cmd)
        else
            e.bad[p.arg] = e.bad[p.arg] or {}
            e.bad[p.arg][p.cmd] = true
            -- side effect on a cockpit switch: put it back with the switch's own clickable command
            local cockpit = GetDevice(0)
            if cockpit and UCS.profile and p.switches then
                for a, old in pairs(p.switches) do
                    local now = readArg(cockpit, a)
                    local def = UCS.profile.args[a]
                    if def and old and now and math.abs(now - old) > 0.01 then
                        pcall(function() GetDevice(def.dev):performClickableAction(def.cmd, old) end)
                        send("DIAG|External state: key command " .. p.cmd .. " moved cockpit switch " .. a .. " - restored")
                    end
                end
            end
            -- side effect on another argument of the same script: press again to undo (toggle)
            for arg, before in pairs(p.before) do
                local now = e.loc[arg]
                if arg ~= p.arg and now and math.abs(now.v - before) > 0.3 then
                    pcall(LoSetCommand, p.cmd, 1)
                    send("DIAG|External state: key command " .. p.cmd .. " changed arg " .. arg .. " instead of " .. p.arg .. " - undone")
                    break
                end
            end
        end
        e.pending = nil
        e.nextApply = t + 1.0
        return
    end

    -- 4) apply one difference at a time (settled on both sides, plain on/off-like values)
    if t < e.nextApply or not e.primed then return end
    for arg, r in pairs(e.remote) do
        local l = e.loc[arg]
        if l and not e.blocked[arg] and t - (e.remoteAt[arg] or t) >= 1.0 and extSettled(e, arg, t, 1.5)
            and l.v >= -0.05 and r >= -0.05 and math.abs(l.v - r) > 0.3
            and (e.remoteAt[arg] or 0) > (e.ownAt[arg] or -1) then
            local g = e.byArg[arg]
            -- Only commands known to change this argument: the ones the script itself ties to it, or the
            -- single command of a script. Ambiguous cases are left alone (never guess in a crewed aircraft).
            local cands = g.prefer[arg]
            if not cands and not g.hasPrefer and #g.cmds == 1 then cands = g.cmds end
            local cmd = e.learned[arg]
            if not cmd and cands then
                for _, c in ipairs(cands) do
                    if not (e.bad[arg] and e.bad[arg][c]) then cmd = c; break end
                end
            end
            if not cands then e.blocked[arg] = true; return end
            e.tries[arg] = (e.tries[arg] or 0) + 1
            if not cmd or e.tries[arg] > 8 then
                e.blocked[arg] = true
                send("DIAG|External state: arg " .. arg .. " could not be synced (no key command of the mod changes it)")
            else
                local before = {}
                for _, a in ipairs(g.args) do if e.loc[a] then before[a] = e.loc[a].v end end
                local switches = {}
                local cockpit = GetDevice(0)
                if cockpit and UCS.profile then
                    for a, _ in pairs(UCS.profile.args) do switches[a] = readArg(cockpit, a) end
                end
                pcall(LoSetCommand, cmd, 1)
                for _, a in ipairs(g.args) do e.appliedAt[a] = t end
                e.pending = { arg = arg, cmd = cmd, at = t, before = before, switches = switches }
            end
            return
        end
    end
end
UCS.extReset = extReset
extReset()

-- -----------------------------------------------------------------------------
-- Diagnostics
-- -----------------------------------------------------------------------------
diagOpen = function()
    local ok, err = pcall(function()
        local lfs = require("lfs")
        local dir = lfs.writedir() .. [[Logs\UCS]]
        pcall(lfs.mkdir, dir) -- keeps UCS files together (no error if it already exists)
        local name = dir .. [[\UCS-diag-]] .. os.date("%Y%m%d-%H%M%S") .. ".csv"
        local file = io.open(name, "w")
        if not file then error("cannot create " .. name) end
        local cols = { "wall", "model_time", "frame_ms", "mirroring", "paused", "master", "seat" }
        for i = 1, #UCS.flight do
            local f = UCS.flight[i]
            table.insert(cols, "a" .. f.arg .. "_" .. f.axis .. "_local")
            table.insert(cols, "a" .. f.arg .. "_" .. f.axis .. "_remote")
        end
        for _, axis in ipairs({ "pitch", "roll", "yaw", "collective" }) do table.insert(cols, "inj_" .. axis) end
        file:write(table.concat(cols, ",") .. "\n")
        UCS.diag = { file = file, name = name, bytes = 0, last = nil, prev = {}, prev2 = {}, flips = {}, lo = {}, hi = {}, nextReport = wall() + 5 }
        log("Diagnostic CSV: " .. name)
        send("DIAG|Recording " .. name)
    end)
    if not ok then
        log("diagnostic open failed: " .. tostring(err))
        send("DIAG|Could not create the diagnostic file: " .. tostring(err))
        UCS.diag = nil
    end
end

diagClose = function()
    if UCS.diag and UCS.diag.file then
        pcall(function() UCS.diag.file:close() end)
        send("DIAG|Diagnostic file closed: " .. UCS.diag.name)
    end
    UCS.diag = nil
end

-- Counts direction reversals frame by frame: a value bouncing between two positions
-- means two sources write the same animation argument.
local function trackFlicker(d, arg, v)
    local p, p2 = d.prev[arg], d.prev2[arg]
    if p and p2 then
        local d1, d2 = p - p2, v - p
        if math.abs(d1) > 0.02 and math.abs(d2) > 0.02 and (d1 > 0) ~= (d2 > 0) then
            d.flips[arg] = (d.flips[arg] or 0) + 1
        end
    end
    d.prev2[arg] = p
    d.prev[arg] = v
    if d.lo[arg] == nil or v < d.lo[arg] then d.lo[arg] = v end
    if d.hi[arg] == nil or v > d.hi[arg] then d.hi[arg] = v end
end

local function pausedAxes(t)
    local out = {}
    for axis, untilT in pairs(UCS.axisPause) do if t < untilT then table.insert(out, axis) end end
    for axis, _ in pairs(UCS.axisOff) do table.insert(out, axis .. "-off") end
    table.sort(out)
    return table.concat(out, " ")
end

local function diagFrame(cockpit, t)
    local d = UCS.diag
    if not d or not d.file then return end
    local frameMs = d.last and (t - d.last) * 1000 or 0
    d.last = t
    local okm, mt = pcall(LoGetModelTime)
    local row = {
        string.format("%.3f", t), string.format("%.3f", (okm and mt) or 0), string.format("%.1f", frameMs),
        UCS.mirroring and "1" or "0", pausedAxes(t),
        UCS.master == nil and "" or (UCS.master and "1" or "0"), UCS.seat == nil and "" or tostring(UCS.seat),
    }
    for i = 1, #UCS.flight do
        local f = UCS.flight[i]
        local v = readArg(cockpit, f.arg)
        if v then trackFlicker(d, f.arg, v) end
        table.insert(row, v and string.format("%.4f", v) or "")
        local rv = UCS.fremote[f.arg]
        table.insert(row, rv and string.format("%.4f", rv) or "")
    end
    for _, axis in ipairs({ "pitch", "roll", "yaw", "collective" }) do
        local iv = UCS.injected[axis]
        table.insert(row, iv and string.format("%.4f", iv) or "")
    end
    -- Switch args too (flicker counting only, not written to the CSV)
    if UCS.profile then
        for arg, _ in pairs(UCS.profile.args) do
            local v = readArg(cockpit, arg)
            if v then trackFlicker(d, arg, v) end
        end
    end
    local line = table.concat(row, ",") .. "\n"
    if d.bytes < 200 * 1024 * 1024 then
        d.file:write(line)
        d.bytes = d.bytes + #line
    end
    if t >= d.nextReport then
        d.nextReport = t + 5
        pcall(function() d.file:flush() end)
        local found = {}
        for arg, n in pairs(d.flips) do
            if n >= 4 then
                table.insert(found, string.format("%d (%d flips, %.2f..%.2f)", arg, n, d.lo[arg] or 0, d.hi[arg] or 0))
            end
        end
        if #found > 0 then
            send("DIAG|Flickering args in the last 5 s: " .. table.concat(found, "; "))
        end
        d.flips, d.lo, d.hi = {}, {}, {}
    end
end

-- -----------------------------------------------------------------------------
-- Export callbacks (chained)
-- -----------------------------------------------------------------------------
local prevStart = LuaExportStart
local prevAfter = LuaExportAfterNextFrame
local prevStop  = LuaExportStop

LuaExportStart = function()
    if prevStart then pcall(prevStart) end
    local ok, err = pcall(function()
        package.path  = package.path  .. ";.\\LuaSocket\\?.lua"
        package.cpath = package.cpath .. ";.\\LuaSocket\\?.dll"
        UCS.socket = require("socket")
        UCS.udp = UCS.socket.udp()
        UCS.udp:setsockname(UCS.host, UCS.listenPort)
        UCS.udp:settimeout(0)
    end)
    if not ok then
        log("socket init failed: " .. tostring(err))
        UCS.udp = nil
        return
    end
    UCS.profile = nil
    UCS.active = false
    UCS.selfName = getSelfName()
    send("HELLO|" .. UCS.version)
    reportSelf()
    log("UCS-Export " .. UCS.version .. " started")
end

LuaExportAfterNextFrame = function()
    if prevAfter then pcall(prevAfter) end
    if not UCS.udp then return end
    local ok, err = pcall(function()
        local t = now()
        receiveAll()
        if t >= UCS.nextSelf then
            UCS.nextSelf = t + UCS.selfInterval
            local name = getSelfName()
            if name ~= UCS.selfName then
                UCS.selfName = name
                if UCS.profile and name ~= "" and string.lower(name) ~= string.lower(UCS.profile.aircraft) then
                    UCS.profile = nil
                    goStandby("aircraft changed to " .. name)
                end
            end
            reportSelf()
        end
        if UCS.active and t >= UCS.nextPoll then
            UCS.nextPoll = t + UCS.pollInterval
            scanArguments()
        end
        if UCS.active then
            local cockpit = GetDevice(0)
            if cockpit then
                local w = wall()
                updateControl(w)
                extFrame(w)
                sendFlight(cockpit, w)
                injectFlight(cockpit, w)
                if UCS.diag then diagFrame(cockpit, w) end
            end
        end
    end)
    if not ok then log("frame error: " .. tostring(err)) end
end

LuaExportStop = function()
    pcall(function()
        send("BYE")
        if UCS.udp then UCS.udp:close() end
    end)
    diagClose()
    UCS.fdiag = false
    UCS.finject = false
    UCS.mirroring = false
    UCS.udp = nil
    UCS.active = false
    UCS.profile = nil
    if prevStop then pcall(prevStop) end
end
