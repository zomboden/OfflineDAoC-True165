extends RefCounted
## 1.64 arrow object 512. Flight uses the server's duration and the arrow attaches only on a confirmed hit.
const ARROW_MODEL := 512
const LIFETIME_SECONDS := 30.0
const MAX_EMBEDDED := 6
static var _arrow_scene: PackedScene
const TIP_X := 0.98   # positive X tip of the converted original arrow mesh, in metres


static func flight_seconds(stance: int) -> float:
	return 0.0 if stance <= 0 else 0.35 + (stance - 1) * 0.075


static func actor_point(actor: Node3D, bone_name: String) -> Vector3:
	var skeleton := SpellFx._skeleton(actor)
	var bone := skeleton.find_bone(bone_name) if skeleton else -1
	if bone >= 0:
		return skeleton.global_transform * skeleton.get_bone_global_pose(bone).origin
	var height := (actor as Npc).height if actor is Npc else 1.8
	return actor.global_position + Vector3.UP * height * 0.65


static func aim_basis(direction: Vector3) -> Basis:
	var x := direction.normalized()
	if x.length_squared() < 0.0001:
		x = Vector3.RIGHT
	var up := Vector3.UP if absf(x.dot(Vector3.UP)) < 0.98 else Vector3.FORWARD
	var z := x.cross(up).normalized()
	return Basis(x, z.cross(x).normalized(), z)


static func arrow() -> Node3D:
	if _arrow_scene == null:
		_arrow_scene = WeaponModels.pack(WeaponModels.path_of(ARROW_MODEL))
	return _arrow_scene.instantiate() as Node3D if _arrow_scene else null


static func launch(attacker: Node3D, target: Node3D, stance: int) -> void:
	if not is_instance_valid(attacker) or not is_instance_valid(target) or not attacker.is_inside_tree() or not target.is_inside_tree():
		return
	var seconds := flight_seconds(stance)
	if seconds <= 0.0:
		return
	var shaft := arrow()
	if shaft == null:
		return
	var start := actor_point(attacker, "Bip01 R Held")
	var end := actor_point(target, "Bip01 Spine2")
	var basis := aim_basis(end - start)
	var scene := attacker.get_tree().current_scene
	if scene == null:
		shaft.free()
		return
	scene.add_child(shaft)
	shaft.global_transform = Transform3D(basis, start - basis.x * TIP_X)
	var travel := shaft.create_tween()
	travel.tween_property(shaft, "global_position", end - basis.x * TIP_X, seconds)
	travel.tween_callback(shaft.queue_free)


static func embed(attacker: Node3D, target: Node3D) -> void:
	if not is_instance_valid(target) or not target.is_inside_tree():
		return
	var parent: Node3D = target
	var skeleton := SpellFx._skeleton(target)
	if skeleton and skeleton.find_bone("Bip01 Spine2") >= 0:
		parent = skeleton
	var rack := parent.get_node_or_null("EmbeddedArrows") as Node3D
	if rack == null:
		if parent is Skeleton3D:
			var mount := BoneAttachment3D.new()
			mount.bone_name = "Bip01 Spine2"
			rack = mount
		else:
			rack = Node3D.new()
		rack.name = "EmbeddedArrows"
		parent.add_child(rack)
	if rack.get_child_count() >= MAX_EMBEDDED:
		var oldest := rack.get_child(0)
		rack.remove_child(oldest)
		oldest.queue_free()
	var shaft := arrow()
	if shaft == null:
		return
	var center := actor_point(target, "Bip01 Spine2")
	var direction := center - actor_point(attacker, "Bip01 R Held") if is_instance_valid(attacker) else -target.global_basis.z
	var basis := aim_basis(direction)
	var tip := center - basis.x * 0.18
	rack.add_child(shaft)
	shaft.global_transform = Transform3D(basis, tip - basis.x * TIP_X)
	target.get_tree().create_timer(LIFETIME_SECONDS).timeout.connect(func():
		if is_instance_valid(shaft):
			shaft.queue_free())
