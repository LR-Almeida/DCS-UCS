-- =============================================================================
--  DCS-UCS (Universal Control Sync) - DCS Server Auto-Connect Hook (optional)
--  Install on the machine that hosts the DCS server:
--    Saved Games\DCS.server\Scripts\Hooks\UCS-ServerHook.lua   (dedicated)
--    Saved Games\DCS\Scripts\Hooks\UCS-ServerHook.lua          (hosting from the game)
--  Config (same folder): UCS-AutoConnect.cfg  ->  "host:port" (e.g. 192.168.1.50:5055)
--
--  When a player connects, a private silent chat message
--  "UCS_AUTOCONNECT:<addr>" is sent only to that player. UCS-Hook.lua on the
--  player's machine forwards it to UCS-Client.exe, which connects automatically
--  (SRS-style auto-connect).
-- =============================================================================

local UCSServer = {
    version     = "0.1.3-alpha",
    defaultPort = 5055,
    prefix      = "UCS_AUTOCONNECT:",
    sendDelay   = 5.0,   -- seconds after connect (player must finish loading)
    address     = nil,
    queue       = {},    -- [playerId] = time to send
    sentSlot    = {},    -- [playerId] = true after the first slot change resend
}

local function log(msg)
    pcall(function() net.log("[DCS-UCS Server] " .. tostring(msg)) end)
end

local function trim(s)
    return (string.gsub(s or "", "^%s*(.-)%s*$", "%1"))
end

local function hookDir()
    local dir = lfs.writedir() .. "Scripts\\Hooks\\"
    return dir
end

-- Returns the configured address ("" = auto-detect) and whether the public IP may be looked up
-- (line "DetectPublicIp=false" turns the api.ipify.org request off; the local IP is used instead).
local function readConfig()
    local path = hookDir() .. "UCS-AutoConnect.cfg"
    local f = io.open(path, "r")
    if not f then
        log("config not found (" .. path .. "), using auto-detection")
        return "", true
    end
    local value = ""
    local detectPublic = true
    for line in f:lines() do
        local l = trim(line)
        if l ~= "" and string.sub(l, 1, 1) ~= "#" and string.sub(l, 1, 1) ~= ";" then
            local opt = string.match(l, "^[Dd]etect[Pp]ublic[Ii]p%s*=%s*(%a+)")
            if opt then
                detectPublic = string.lower(opt) ~= "false" and opt ~= "0" and string.lower(opt) ~= "no"
            elseif value == "" then
                value = l
            end
        end
    end
    f:close()
    return value, detectPublic
end

local function detectAddress(detectPublic)
    local ip = nil
    local ok = pcall(function()
        package.path  = package.path  .. ";.\\LuaSocket\\?.lua"
        package.cpath = package.cpath .. ";.\\LuaSocket\\?.dll"
        local socket = require("socket")
        -- Public IP (short timeout; runs once)
        local okHttp, http = false, nil
        if detectPublic then okHttp, http = pcall(require, "socket.http") end
        if okHttp and http then
            http.TIMEOUT = 3
            local body, code = http.request("http://api.ipify.org")
            if body and code == 200 then
                body = trim(body)
                if string.match(body, "^%d+%.%d+%.%d+%.%d+$") then ip = body end
            end
        end
        -- Local IP fallback
        if not ip then
            local probe = socket.udp()
            probe:setpeername("8.8.8.8", 53) -- no packet is sent for UDP connect
            local localIp = probe:getsockname()
            probe:close()
            if localIp and localIp ~= "0.0.0.0" then ip = localIp end
        end
        if not ip then
            ip = socket.dns.toip(socket.dns.gethostname())
        end
    end)
    if not ok or not ip then ip = "127.0.0.1" end
    return ip
end

local function resolveAddress()
    if UCSServer.address then return UCSServer.address end
    local cfg, detectPublic = readConfig()
    if cfg == "" then
        cfg = detectAddress(detectPublic)
    end
    if not string.find(cfg, ":", 1, true) then
        cfg = cfg .. ":" .. tostring(UCSServer.defaultPort)
    end
    UCSServer.address = cfg
    log("UCS server address: " .. cfg)
    return cfg
end

-- Only a multiplayer host announces a UCS server (DCS.isServer() is also true in single player,
-- where nothing must be contacted).
local function isServer()
    local ok, res = pcall(function()
        return DCS.isServer() and (DCS.isMultiplayer == nil or DCS.isMultiplayer())
    end)
    return ok and res == true
end

local function sendAutoConnect(id)
    local myId = net.get_my_player_id()
    if id == nil or id == myId then return end
    local addr = resolveAddress()
    local ok, err = pcall(function()
        net.send_chat_to(UCSServer.prefix .. addr, id, myId)
    end)
    if not ok then log("send_chat_to failed: " .. tostring(err)) end
end

local callbacks = {}

function callbacks.onMissionLoadEnd()
    if isServer() then
        UCSServer.address = nil
        resolveAddress()
    end
end

function callbacks.onPlayerConnect(id)
    if not isServer() then return end
    UCSServer.queue[id] = os.clock() + UCSServer.sendDelay
    UCSServer.sentSlot[id] = nil
end

function callbacks.onPlayerChangeSlot(id)
    if not isServer() then return end
    -- Resend once on the first slot selection, in case the first message
    -- arrived while the player was still loading.
    if not UCSServer.sentSlot[id] then
        UCSServer.sentSlot[id] = true
        sendAutoConnect(id)
    end
end

function callbacks.onPlayerDisconnect(id)
    UCSServer.queue[id] = nil
    UCSServer.sentSlot[id] = nil
end

function callbacks.onSimulationFrame()
    local t = os.clock()
    for id, when in pairs(UCSServer.queue) do
        if t >= when then
            UCSServer.queue[id] = nil
            sendAutoConnect(id)
        end
    end
end

DCS.setUserCallbacks(callbacks)
log("UCS-ServerHook " .. UCSServer.version .. " loaded")
