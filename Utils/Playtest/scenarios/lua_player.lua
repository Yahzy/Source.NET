-- Player and entity Lua methods used by the stock weapons and gamemodes.

PT.Server("give weapons", function()
	local ply = Entity(1)
	ply:Give("weapon_pistol")
	ply:Give("weapon_medkit")
	ply:Give("manhack_welder")
	ply:Give("weapon_flechettegun")
	PT.Log("weapons " .. #ply:GetWeapons())
end)
PT.Wait(1)

PT.Server("fire lua weapons", function()
	local ply = Entity(1)
	for _, class in ipairs({ "weapon_medkit", "manhack_welder", "weapon_flechettegun" }) do
		ply:SelectWeapon(class)
		local wep = ply:GetWeapon(class)
		if not IsValid(wep) then error("missing " .. class) end
		wep:PrimaryAttack()
	end
end)
PT.Wait(1)

PT.Shared("muzzle flash", function()
	if SERVER then Entity(1):MuzzleFlash() else LocalPlayer():MuzzleFlash() end
end)

PT.Server("is npc", function()
	local npc = ents.Create("npc_citizen")
	npc:SetPos(Entity(1):GetPos() + Vector(200, 0, 10))
	npc:Spawn()
	if not npc:IsNPC() then error("npc_citizen:IsNPC() returned false") end
	if Entity(1):IsNPC() then error("player:IsNPC() returned true") end
	if game.GetWorld():IsNPC() then error("world:IsNPC() returned true") end
end)
PT.Wait(3)
PT.Client("is npc on client", function()
	local npcs = 0
	for _, ent in ipairs(ents.GetAll()) do
		if ent:IsNPC() then npcs = npcs + 1 end
	end
	if npcs == 0 then error("no entity reports IsNPC() on the client") end
	if LocalPlayer():IsNPC() then error("LocalPlayer():IsNPC() returned true") end
end)

PT.Server("lag compensation", function()
	local ply = Entity(1)
	ply:LagCompensation(true)
	ply:LagCompensation(false)
end)

PT.Server("strip weapons", function()
	local ply = Entity(1)
	ply:StripWeapons()
	local count = #ply:GetWeapons()
	if count ~= 0 then error("still has " .. count .. " weapons") end
	if IsValid(ply:GetActiveWeapon()) then error("active weapon still set") end
end)
PT.Wait(1)
PT.Shot("stripped")
PT.Done()

PT.Start()
