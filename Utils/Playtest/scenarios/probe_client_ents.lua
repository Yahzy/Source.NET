-- Lists what the client sees after an NPC is spawned.

PT.Server("spawn", function()
	local npc = ents.Create("npc_citizen")
	npc:SetPos(Entity(1):GetPos() + Entity(1):GetForward() * 200 + Vector(0, 0, 10))
	npc:Spawn()
	PT.Log("server index " .. npc:EntIndex())
end)
PT.Wait(2)
PT.Client("list", function()
	local counts = {}
	for _, ent in ipairs(ents.GetAll()) do
		local c = ent:GetClass()
		counts[c] = (counts[c] or 0) + 1
	end
	local parts = {}
	for c, n in SortedPairs(counts) do parts[#parts + 1] = c .. "=" .. n end
	PT.Log("client ents " .. #ents.GetAll() .. ": " .. table.concat(parts, " "))
	for i = 1, 200 do
		local e = Entity(i)
		if IsValid(e) and not e:IsPlayer() then
			PT.Log("Entity(" .. i .. ") " .. tostring(e) .. " class=" .. e:GetClass() .. " npc=" .. tostring(e:IsNPC()))
		end
	end
end)
PT.Shot("gman")
PT.Done()

PT.Start()
