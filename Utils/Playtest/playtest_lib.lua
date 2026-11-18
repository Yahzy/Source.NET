-- Scenario runner used by playtest.py.
-- The same file is loaded in both realms. Each realm walks the same timeline
-- and only executes the actions that belong to it, so waits stay aligned.

PT = PT or {}

local realm = SERVER and "sv" or "cl"
local timeline = {}

local function Mark(kind, text)
	print("##PT_" .. kind .. "## [" .. realm .. "] " .. tostring(text or ""))
end

function PT.Log(text) Mark("LOG", text) end

-- Tells the harness which entity class the current item created, so a later
-- crash in that class can be traced back to the item.
function PT.ItemClass(class) Mark("ICLASS", class) end

local function Push(kind, arg, fn)
	timeline[#timeline + 1] = { kind = kind, arg = arg, fn = fn }
end

function PT.Wait(seconds) Push("wait", seconds) end
function PT.Server(name, fn) Push("sv", name, fn) end
function PT.Client(name, fn) Push("cl", name, fn) end
function PT.Shared(name, fn) Push("sh", name, fn) end
function PT.Cmd(...) local args = { ... } Push("cl", "cmd " .. table.concat(args, " "), function() RunConsoleCommand(unpack(args)) end) end
function PT.Shot(name) Push("shot", name) end
function PT.Done() Push("done") end

local function RunAction(name, fn)
	local ok, err = xpcall(fn, function(e) return debug and debug.traceback and debug.traceback(tostring(e), 2) or tostring(e) end)
	if ok then
		Mark("STEP", name)
	else
		Mark("FAIL", name .. " :: " .. tostring(err))
	end
end

-- Runs fn(key, value) for every entry, one entry per delay, in this realm only.
-- Items listed in PT.Skip (filled by the harness after a crash) are left out.
PT.Skip = PT.Skip or {}

function PT.ForEach(category, tbl, fn, delay)
	delay = delay or 0.25
	local i = 0
	for key, value in SortedPairs(tbl) do
		local id = category .. ":" .. tostring(key)
		if PT.Skip[id] then
			Mark("LOG", "skipped " .. id)
		else
			i = i + 1
			timer.Simple(i * delay, function()
				Mark("ITEM", id)
				local ok, err = pcall(fn, key, value)
				if not ok then Mark("IFAIL", id .. " :: " .. tostring(err)) end
			end)
		end
	end
	Mark("LOG", "queued " .. i .. " " .. category)
	return i * delay
end

local shotDelay = 0.15

local function Step(index)
	local item = timeline[index]
	if not item then return end

	local nextDelay = 0
	if item.kind == "wait" then
		nextDelay = item.arg
	elseif item.kind == "shot" then
		if CLIENT then
			-- Several captures per shot: the harness keeps the best one
			for i, suffix in ipairs({ "a", "b", "c" }) do
				timer.Simple((i - 1) * shotDelay, function() RunConsoleCommand("screenshot", "pt_" .. item.arg .. "_" .. suffix) end)
			end
			Mark("SHOT", item.arg)
		end
		nextDelay = shotDelay * 3 + 0.2
	elseif item.kind == "done" then
		Mark("DONE", "")
		return
	elseif item.kind == "sh" or item.kind == realm then
		RunAction(item.arg, item.fn)
	end

	timer.Simple(nextDelay, function() Step(index + 1) end)
end

local function IsReady()
	if CLIENT then return IsValid(LocalPlayer()) end
	return IsValid(Entity(1))
end

function PT.Start(settle)
	settle = settle or 3
	local function Poll()
		if IsReady() then
			Mark("READY", "")
			timer.Simple(settle, function() Step(1) end)
		else
			timer.Simple(0.5, Poll)
		end
	end
	timer.Simple(0.5, Poll)
end
