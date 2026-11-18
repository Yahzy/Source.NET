-- NPCs that used to crash once their AI started selecting schedules.

local function Spawn(class, weapon, offset)
	local ply = Entity(1)
	local ent = ents.Create(class)
	ent:SetPos(ply:GetPos() + ply:GetForward() * 300 + offset)
	if weapon then ent:SetKeyValue("additionalequipment", weapon) end
	ent:Spawn()
	ent:Activate()
	return ent
end

PT.Server("spawn npcs", function()
	Spawn("npc_gman", nil, Vector(0, -150, 10))
	Spawn("npc_metropolice", "weapon_stunstick", Vector(0, 0, 10))
	Spawn("npc_metropolice", "weapon_pistol", Vector(0, 150, 10))
	Spawn("npc_metropolice", "weapon_smg1", Vector(150, 0, 10))
end)
PT.Wait(5)
PT.Shot("npcs_spawned")
PT.Wait(15)
PT.Shot("npcs_combat")
PT.Server("player status", function()
	PT.Log("player health " .. Entity(1):Health())
end)
PT.Done()

PT.Start()
