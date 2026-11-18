-- Rebels against metro police: exercises the combat AI of both sides.

local function Spawn(class, weapon, offset, keyvalues)
	PT.ItemClass(class)
	local ply = Entity(1)
	local ent = ents.Create(class)
	ent:SetPos(ply:GetPos() + ply:GetForward() * 400 + offset)
	if weapon then ent:SetKeyValue("additionalequipment", weapon) end
	for k, v in pairs(keyvalues or {}) do ent:SetKeyValue(k, v) end
	ent:Spawn()
	ent:Activate()
	return ent
end

local fighters = {
	rebel_smg = { "npc_citizen", "weapon_smg1", Vector(0, -300, 10), { citizentype = "3" } },
	rebel_pistol = { "npc_citizen", "weapon_pistol", Vector(100, -300, 10), { citizentype = "3" } },
	cop_pistol = { "npc_metropolice", "weapon_pistol", Vector(0, 300, 10) },
	cop_stick = { "npc_metropolice", "weapon_stunstick", Vector(100, 300, 10) },
	cop_smg = { "npc_metropolice", "weapon_smg1", Vector(200, 300, 10) },
}

PT.Server("spawn fighters", function()
	PT.ForEach("fighter", fighters, function(name, f)
		Spawn(f[1], f[2], f[3], f[4])
	end, 0.2)
end)
PT.Wait(8)
PT.Shot("battle_start")
PT.Wait(25)
PT.Shot("battle_end")
PT.Server("survivors", function()
	local alive = 0
	for _, ent in ipairs(ents.GetAll()) do
		if ent:IsNPC() and ent:Health() > 0 then alive = alive + 1 end
	end
	PT.Log("npcs alive " .. alive .. ", player health " .. Entity(1):Health())
end)
PT.Done()

PT.Start()
