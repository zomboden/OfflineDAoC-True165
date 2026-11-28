extends Control

const WIDTH := 210.0
const HEIGHT := 29.0
const TRACK_WIDTH := WIDTH - 18.0
var _caption: Label
var _fill: ColorRect
var _started := 0
var _duration := 0.0


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	size = Vector2(WIDTH, HEIGHT)
	add_child(ExchangeWindow._granite(size))
	_caption = UiArt.label("", 10)
	_caption.position = Vector2(8, 4)
	_caption.size = Vector2(WIDTH - 16.0, 13)
	_caption.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_caption.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	add_child(_caption)
	var track := ColorRect.new()
	track.color = Color(0.035, 0.03, 0.025, 0.94)
	track.position = Vector2(9, 19)
	track.size = Vector2(TRACK_WIDTH, 5)
	track.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(track)
	_fill = ColorRect.new()
	_fill.color = UiArt.GOLD
	_fill.size = Vector2(0, track.size.y)
	_fill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	track.add_child(_fill)
	update_cast("", 0.0)


func update_cast(spell_name: String, seconds: float) -> void:
	visible = seconds > 0.0
	set_process(visible)
	if visible:
		_caption.text = ("Casting" if spell_name.is_empty() else spell_name).to_upper()
		_started = Time.get_ticks_msec()
		_duration = seconds * 1000.0
		_fill.size.x = 0.0


func _process(_delta: float) -> void:
	var progress := (Time.get_ticks_msec() - _started) / _duration
	_fill.size.x = TRACK_WIDTH * clampf(progress, 0.0, 1.0)
	if progress >= 1.0:
		update_cast("", 0.0)
