-- =============================================================================
--  DCS-UCS (Universal Control Sync) - Client GameGUI Hook
--  Install: Saved Games\DCS\Scripts\Hooks\UCS-Hook.lua
--
--  * Reports the current DCS server / group / unit / aircraft type to the
--    UCS-Client.exe (UDP 127.0.0.1:5053) whenever the player enters, changes
--    slot or leaves an aircraft (auto-pairing).
--  * Intercepts "UCS_AUTOCONNECT:<addr>" private chat messages sent by
--    UCS-ServerHook.lua and forwards them to UCS-Client.exe.
-- =============================================================================

local UCSHook = {
    version      = "0.1.1-alpha",
    clientHost   = "127.0.0.1",
    clientPort   = 5053,
    prefix       = "UCS_AUTOCONNECT:",
    pollInterval = 2.0,   -- seconds between slot checks
    keepAlive    = 10.0,  -- resend the current state even if unchanged
}

UCSHook.udp       = nil
UCSHook.lastState = nil
UCSHook.lastSent  = 0
UCSHook.nextPoll  = 0

local function log(msg)
    pcall(function() net.log("[DCS-UCS] " .. tostring(msg)) end)
end

-- Minimal JSON encoder (strings, numbers, booleans, flat tables)
local function jsonString(s)
    s = tostring(s)
    s = string.gsub(s, '[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == '\\' then return '\\\\' end
        if c == '\n' then return '\\n' end
        if c == '\r' then return '\\r' end
        if c == '\t' then return '\\t' end
        return string.format('\\u%04x', string.byte(c))
    end)
    return '"' .. s .. '"'
end

local function toJson(t)
    local parts = {}
    for k, v in pairs(t) do
        local val
        local tv = type(v)
        if tv == "boolean" then
            val = v and "true" or "false"
        elseif tv == "number" then
            val = tostring(v)
        else
            val = jsonString(v)
        end
        table.insert(parts, jsonString(k) .. ":" .. val)
    end
    return "{" .. table.concat(parts, ",") .. "}"
end

local function ensureSocket()
    if UCSHook.udp then return true end
    local ok, err = pcall(function()
        package.path  = package.path  .. ";.\\LuaSocket\\?.lua"
        package.cpath = package.cpath .. ";.\\LuaSocket\\?.dll"
        local socket = require("socket")
        UCSHook.udp = socket.udp()
        UCSHook.udp:settimeout(0)
    end)
    if not ok then
        log("socket init failed: " .. tostring(err))
        UCSHook.udp = nil
        return false
    end
    return true
end

local function sendToClient(tbl)
    if not ensureSocket() then return end
    local payload = toJson(tbl)
    pcall(function() UCSHook.udp:sendto(payload, UCSHook.clientHost, UCSHook.clientPort) end)
end

-- -----------------------------------------------------------------------------
-- DCS information helpers (every call is protected: APIs vary between versions)
-- -----------------------------------------------------------------------------
local function safe(f, ...)
    local ok, r1, r2 = pcall(f, ...)
    if ok then return r1, r2 end
    return nil
end

local function isMultiplayer()
    return safe(function() return DCS.isMultiplayer() end) == true
end

local function getServerAddress()
    if not isMultiplayer() then
        return "LOCAL"
    end
    -- 1) net.get_server_info() (newer builds)
    local info = safe(function() return net.get_server_info() end)
    if type(info) == "table" then
        local host = info.address or info.ip or info.host or info.server_address
        local port = info.port or info.server_port
        if host and host ~= "" then
            if port then return tostring(host) .. ":" .. tostring(port) end
            return tostring(host)
        end
    elseif type(info) == "string" and info ~= "" then
        return info
    end
    -- 2) net.get_server_host() (some builds return host, port)
    local host, port = safe(function() return net.get_server_host() end)
    if type(host) == "string" and host ~= "" then
        if port then return host .. ":" .. tostring(port) end
        return host
    end
    -- 3) Fallback: server host player name + mission name. Every client on the
    --    same DCS server sees the same values, so the key is still shared.
    local serverName = safe(function() return net.get_player_info(1, "name") end) or "server"
    local mission = safe(function() return DCS.getMissionName() end) or "mission"
    return "MP:" .. tostring(serverName) .. "/" .. tostring(mission)
end

local function getPlayerUnitId()
    -- Single player / host: DCS.getPlayerUnit() if available
    local unitId = safe(function() return DCS.getPlayerUnit() end)
    if unitId ~= nil and tostring(unitId) ~= "" and tostring(unitId) ~= "0" then
        return tostring(unitId)
    end
    -- Multiplayer: slot of my player ("123" or "123_2" for multicrew seats)
    local myId = safe(function() return net.get_my_player_id() end)
    if myId then
        local slot = safe(function() return net.get_player_info(myId, "slot") end)
        if slot ~= nil then
            slot = tostring(slot)
            slot = string.gsub(slot, "_%d+$", "")
            if slot ~= "" and slot ~= "0" and tonumber(slot) then
                return slot
            end
        end
    end
    return nil
end

local function getPlayerName()
    local myId = safe(function() return net.get_my_player_id() end)
    local name = nil
    if myId ~= nil then
        name = safe(function() return net.get_player_info(myId, "name") end)
    end
    if (name == nil or name == "") then
        name = safe(function() return DCS.getPlayerName() end)
    end
    if type(name) ~= "string" then return "" end
    return name
end

local function buildState()
    local state = {
        type       = "slot",
        version    = UCSHook.version,
        server     = getServerAddress(),
        group      = "",
        unit       = "",
        aircraft   = "",
        in_aircraft = false,
        player     = getPlayerName(),
    }
    local unitId = getPlayerUnitId()
    if unitId then
        local group = safe(function() return DCS.getUnitProperty(unitId, DCS.UNIT_GROUPNAME) end)
        local unit  = safe(function() return DCS.getUnitProperty(unitId, DCS.UNIT_NAME) end)
        local acft  = safe(function() return DCS.getUnitType(unitId) end)
        if not acft or acft == "" then
            acft = safe(function() return DCS.getUnitProperty(unitId, DCS.UNIT_TYPE) end)
        end
        state.group = group and tostring(group) or ""
        state.unit  = unit and tostring(unit) or ""
        state.aircraft = acft and tostring(acft) or ""
        state.in_aircraft = state.unit ~= ""
    end
    return state
end

local function stateKey(s)
    return table.concat({ s.server, s.group, s.unit, s.aircraft, tostring(s.in_aircraft), s.player }, "|")
end

local function publishState(force)
    local s = buildState()
    local key = stateKey(s)
    local t = os.clock()
    if force or key ~= UCSHook.lastState or (t - UCSHook.lastSent) > UCSHook.keepAlive then
        UCSHook.lastState = key
        UCSHook.lastSent = t
        sendToClient(s)
    end
end

local function publishLeft()
    UCSHook.lastState = nil
    sendToClient({ type = "slot", version = UCSHook.version, server = "", group = "", unit = "", aircraft = "", in_aircraft = false })
end

-- -----------------------------------------------------------------------------
-- Callbacks
-- -----------------------------------------------------------------------------
local callbacks = {}

function callbacks.onSimulationStart()
    UCSHook.nextPoll = 0
    publishState(true)
end

function callbacks.onSimulationStop()
    publishLeft()
end

function callbacks.onPlayerChangeSlot(id)
    local myId = safe(function() return net.get_my_player_id() end)
    if myId == nil or id == myId then
        publishState(true)
    end
end

function callbacks.onSimulationFrame()
    local t = os.clock()
    if t >= UCSHook.nextPoll then
        UCSHook.nextPoll = t + UCSHook.pollInterval
        publishState(false)
    end
end

function callbacks.onChatMessage(message, from)
    if type(message) ~= "string" then return end
    local s, e = string.find(message, UCSHook.prefix, 1, true)
    if not s then return end
    -- Only trust messages coming from the DCS server itself (player id 1)
    -- so other players cannot redirect us to a rogue UCS server.
    if from ~= nil and from ~= 0 and from ~= 1 then
        log("ignored auto-connect from player " .. tostring(from))
        return ""
    end
    local addr = string.sub(message, e + 1)
    addr = string.gsub(addr, "^%s+", "")
    addr = string.gsub(addr, "%s+$", "")
    if addr ~= "" then
        log("auto-connect -> " .. addr)
        sendToClient({ type = "autoconnect", addr = addr })
        publishState(true)
    end
    -- Returning an empty string asks the chat UI not to show this message.
    -- (Support for chat suppression from hooks depends on the DCS build.)
    return ""
end

DCS.setUserCallbacks(callbacks)
log("UCS-Hook " .. UCSHook.version .. " loaded")
