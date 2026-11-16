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
--    Export -> Client
--      FLT|<arg>=<val>;...                       local flight args (~30 Hz, on change)
--      DIAG|<text>                               diagnostic messages for the client log
--      CTRL|<master 1/0/?>|<seat 0-based/?>|<mirroring 0/1>   who has the controls (on change + every 2 s)
--
--  "inject" (experimental) feeds the received stick position into this seat as joystick input
--  (LoSetCommand) so the mod animates the stick like the pilot's. Automatic: it runs only while
--  DCS says this seat is NOT in control of the flight model (LoGetFMData().master == false in
--  multicrew); when that flag is unavailable, only in a seat other than the first one.
--  "diag" writes Saved Games\DCS\Logs\UCS\UCS-diag-<time>.csv (every frame) and reports
--  flickering cockpit arguments (two sources fighting over the same animation).
-- =============================================================================

local UCS = {
    version       = "0.1.3-alpha",
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

local function goStandby(reason)
    if UCS.active then
        log("Standby: " .. tostring(reason))
    end
    UCS.active = false
    UCS.lastValues = {}
    UCS.ignoreUntil = {}
    UCS.fremote = {}
    UCS.fsent = {}
    UCS.injected = {}
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
    -- every frame: the mirror must stop on the very frame this seat takes the controls
    local master, seat = readControl()
    local changed = master ~= UCS.master or seat ~= UCS.seat
    UCS.master, UCS.seat = master, seat
    local mirroring = mirrorAllowed()
    if mirroring ~= UCS.mirroring then
        changed = true
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
    local out = {}
    for i = 1, #UCS.flight do
        local f = UCS.flight[i]
        local v = readArg(cockpit, f.arg)
        if v then
            local last = UCS.fsent[f.arg]
            if last == nil or math.abs(v - last) > 0.002 then
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
-- Calibration: inject with a candidate; if after 0.6 s the animation does not match the received
-- position (needs a clear stick deflection), try the next one. The first that matches is kept.
local function calibrate(f, axis, cands, cal, la, target, t)
    if math.abs(target) < 0.15 then return end         -- centred stick: cannot tell the direction yet
    cal.since = cal.since or t
    if t - cal.since < 0.6 then return end
    local c = cands[cal.idx]
    if math.abs(la - target) < 0.12 then
        cal.ok = true
        log("Flight input mirror: " .. axis .. " calibrated, command " .. c.cmd .. (c.sign < 0 and " inverted" or ""))
        send("DIAG|Flight input mirror: " .. axis .. " uses command " .. c.cmd .. (c.sign < 0 and " (inverted)" or "") ..
            " (arg " .. f.arg .. ")")
        return
    end
    cal.idx = cal.idx + 1
    cal.since = nil
    if cal.idx > #cands then
        UCS.axisOff[axis] = true
        send("DIAG|Flight input mirror: " .. axis .. " DISABLED - no candidate command moves arg " .. f.arg ..
            string.format(" (local %.2f vs received %.2f). Set FlightAxisCommands in client_config.json.", la, target))
    end
end

local function injectFlight(cockpit, t)
    if not UCS.mirroring or #UCS.flight == 0 then return end
    if t - UCS.fremoteAt > 0.5 then return end          -- stream stopped: leave inputs alone
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
                elseif math.abs(la - target) > 0.25 then
                    -- Safety (per axis): the animation disagrees for more than 1 s -> someone moves the
                    -- local control: pause that axis for 3 s (3 pauses in a row: disable it).
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
            if not paused and not UCS.axisOff[axis] then
                local c = cands[cal.idx]
                local cmdValue = target * c.sign
                pcall(LoSetCommand, c.cmd, cmdValue)
                UCS.injected[axis] = cmdValue
            end
        end
    end
end


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
