extends Node3D

const Archery := preload("res://game/combat/archery_visual.gd")
var failures := 0


class TestActor extends Node3D:
	var model: Node3D


func _ready() -> void:
	call_deferred("_run")


func check(ok: bool, label: String) -> void:
	print(("PASS " if ok else "FAIL ") + label)
	failures += int(not ok)


func _run() -> void:
	var actors := self

	var hammer := WeaponModels.effect_frame(1026, WeaponModels.TO_NIF)
	check(absf(hammer.origin.z - 22.448) < 0.001 and hammer.basis.x.x < -39.0,
		"native hammer socket offsets and rotates the effect")
	var staff := WeaponModels.effect_frame(1171, WeaponModels.TO_NIF)
	check(staff.origin.z > 38.0, "staff glow rides up the shaft")
	check(WeaponModels.effect_frame(971, WeaponModels.TO_NIF).origin.x > 6.5, "claw socket")
	check(WeaponModels.effect_frame(-1, Transform3D.IDENTITY) == Transform3D.IDENTITY, "custom weapon fallback")
	var cases := {3: 1.40, 12: 0.85, 4: 1.40, 13: 0.70}
	var actor := TestActor.new()
	actor.model = Node3D.new()
	actor.add_child(actor.model)
	actors.add_child(actor)
	var models: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://content/monsters/monsters.json"))
	for id in cases:
		var base := float(models[str(id)].scale)
		actor.model.set_meta("period_fx_scale", base)
		var giant_prop := MeshInstance3D.new()
		giant_prop.mesh = BoxMesh.new()
		giant_prop.scale = Vector3.ONE * 200.0
		actor.model.add_child(giant_prop)
		check(is_equal_approx(SpellFx.size_of(actor), cases[id]), "original-size effect for model %d ignores attached mesh" % id)
		giant_prop.free()

	check(is_equal_approx(Archery.flight_seconds(1), 0.35), "arrow minimum flight duration")
	check(is_equal_approx(Archery.flight_seconds(5), 0.65), "arrow wire duration")
	check(Archery.aim_basis(Vector3.FORWARD).x.is_equal_approx(Vector3.FORWARD), "arrow tip faces the target")
	check(Archery.LIFETIME_SECONDS == 30.0, "embedded arrows have a 30-second lifetime")
	var shaft: Node3D = Archery.arrow()
	check(shaft != null, "original arrow model can instantiate")
	if shaft:
		shaft.free()
	for i in range(8):
		Archery.embed(null, actor)
	check(actor.get_node("EmbeddedArrows").get_child_count() == 6, "per-target arrow cap")
	check(ServerWorld.weapon_poison_impact(4080, false, false), "lethal poison lands target-only")
	check(ServerWorld.weapon_poison_impact(873, false, false), "shared poison effect can land target-only")
	check(not ServerWorld.weapon_poison_impact(873, true, false), "shared timed cast preserves caster visuals")
	check(not ServerWorld.weapon_poison_impact(2643, false, true), "shared instant spell preserves handling")
	check(not ServerWorld.weapon_poison_impact(4, false, false), "ordinary heal handling unchanged")
	get_tree().quit(failures)
