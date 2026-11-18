-- Exercise as much of the sandbox content as possible and let the harness collect every error.
-- Each item is created on its own tick so that think and render code runs for it too.

local function SpawnOrigin()
	local ply = Entity(1)
	return ply:GetPos() + ply:GetForward() * 200 + Vector(0, 0, 20)
end

local spawned = {}

local function Create(class)
	PT.ItemClass(class)
	local ent = ents.Create(class)
	if not IsValid(ent) then error("ents.Create returned NULL for " .. tostring(class)) end
	spawned[#spawned + 1] = ent
	return ent
end

local function Cleanup()
	for _, ent in ipairs(spawned) do
		if IsValid(ent) then ent:Remove() end
	end
	spawned = {}
end

local function ApplyKeyValues(ent, kv)
	for k, v in pairs(kv or {}) do ent:SetKeyValue(k, v) end
end

-- 1. Scripted and engine entities from the spawn menu
PT.Server("spawnable entities", function()
	PT.ForEach("entity", list.Get("SpawnableEntities"), function(class, info)
		local ent = Create(info.ClassName or class)
		ApplyKeyValues(ent, info.KeyValues)
		ent:SetPos(SpawnOrigin())
		ent:Spawn()
		ent:Activate()
	end)
end)
PT.Wait(12)
PT.Shot("entities")
PT.Server("cleanup entities", Cleanup)

-- 2. Vehicles
PT.Server("vehicles", function()
	PT.ForEach("vehicle", list.Get("Vehicles"), function(name, info)
		local ent = Create(info.Class)
		ent:SetModel(info.Model)
		ApplyKeyValues(ent, info.KeyValues)
		ent:SetPos(SpawnOrigin())
		ent:Spawn()
		ent:Activate()
	end)
end)
PT.Wait(6)
PT.Shot("vehicles")
PT.Server("cleanup vehicles", Cleanup)

-- 3. NPCs
PT.Server("npcs", function()
	PT.ForEach("npc", list.Get("NPC"), function(name, info)
		local ent = Create(info.Class)
		if info.Model then ent:SetModel(info.Model) end
		ApplyKeyValues(ent, info.KeyValues)
		ent:SetPos(SpawnOrigin())
		ent:Spawn()
		ent:Activate()
		if info.Weapons and info.Weapons[1] and ent.Give then ent:Give(info.Weapons[1]) end
	end)
end)
PT.Wait(18)
PT.Shot("npcs")
PT.Server("cleanup npcs", Cleanup)

-- 4. Weapons: give and fire each one
PT.Server("ammo", function()
	local ply = Entity(1)
	for _, ammo in ipairs({ "Pistol", "SMG1", "Buckshot", "357", "AR2", "XBowBolt", "RPG_Round", "SMG1_Grenade", "Grenade" }) do
		ply:GiveAmmo(100, ammo)
	end
end)
PT.Server("weapons", function()
	local ply = Entity(1)
	local spawnable = {}
	for class, info in pairs(list.Get("Weapon")) do
		if info.Spawnable then spawnable[class] = info end
	end
	PT.ForEach("weapon", spawnable, function(class)
		PT.ItemClass(class)
		ply:Give(class)
		ply:SelectWeapon(class)
		local wep = ply:GetActiveWeapon()
		if IsValid(wep) then
			if wep.PrimaryAttack then wep:PrimaryAttack() end
			if wep.SecondaryAttack then wep:SecondaryAttack() end
		end
	end, 0.4)
end)
PT.Wait(18)
PT.Shot("weapons")
PT.Server("strip weapons", function()
	local ply = Entity(1)
	ply:StripWeapons()
	ply:Give("gmod_tool")
	ply:SelectWeapon("gmod_tool")
end)

-- 5. Tool gun: every tool mode, one click on the world
PT.Wait(1)
PT.Client("tools", function()
	local stored = weapons.GetStored("gmod_tool")
	local modes = {}
	for name in pairs(stored and stored.Tool or {}) do modes[name] = true end
	PT.ForEach("tool", modes, function(mode)
		RunConsoleCommand("gmod_toolmode", mode)
		timer.Simple(0.1, function() RunConsoleCommand("+attack") end)
		timer.Simple(0.2, function() RunConsoleCommand("-attack") end)
	end, 0.35)
end)
PT.Wait(25)
PT.Shot("tools")

-- 6. Menus
PT.Cmd("+menu")
PT.Wait(2)
PT.Shot("spawnmenu")
PT.Cmd("-menu")
PT.Cmd("+menu_context")
PT.Wait(1.5)
PT.Shot("contextmenu")
PT.Cmd("-menu_context")
PT.Cmd("+showscores")
PT.Wait(1)
PT.Shot("scoreboard")
PT.Cmd("-showscores")

PT.Wait(1)
PT.Done()

PT.Start(4)
