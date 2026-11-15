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
-- =============================================================================

local UCS = {
    version       = "1.0.0",
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
            if def then
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
    end)
    if not ok then log("frame error: " .. tostring(err)) end
end

LuaExportStop = function()
    pcall(function()
        send("BYE")
        if UCS.udp then UCS.udp:close() end
    end)
    UCS.udp = nil
    UCS.active = false
    UCS.profile = nil
    if prevStop then pcall(prevStop) end
end
