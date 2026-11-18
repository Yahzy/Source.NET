-- Basic sanity run: the map loads, the player spawns, props can be created and the view renders.

PT.Shot("spawn")

PT.Server("spawn props", function()
	local ply = Entity(1)
	local origin = ply:GetPos() + ply:GetForward() * 150
	for i, model in ipairs({ "models/props_c17/oildrum001.mdl", "models/props_junk/wood_crate001a.mdl", "models/props_borealis/bluebarrel001.mdl" }) do
		local ent = ents.Create("prop_physics")
		ent:SetModel(model)
		ent:SetPos(origin + Vector(0, (i - 2) * 60, 40))
		ent:Spawn()
	end
end)

PT.Wait(2)
PT.Shot("props")

PT.Server("give weapons", function()
	local ply = Entity(1)
	ply:Give("weapon_physgun")
	ply:Give("gmod_tool")
	ply:Give("weapon_crowbar")
end)

PT.Wait(1)
PT.Shot("weapons")
PT.Done()

PT.Start()
