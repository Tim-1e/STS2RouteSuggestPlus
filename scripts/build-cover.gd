extends SceneTree

func fail(message: String) -> void:
	push_error(message)
	quit(1)

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		fail("Expected source PNG and output PCK paths")
		return
	var source := Image.load_from_file(args[0])
	if source == null or source.is_empty():
		fail("Cannot load cover: " + args[0])
		return
	var texture := ImageTexture.create_from_image(source)
	if ResourceSaver.save(texture, "res://mod_image.res", ResourceSaver.FLAG_COMPRESS) != OK:
		fail("Cannot save cover texture")
		return
	var remap := FileAccess.open("res://mod_image.png.remap", FileAccess.WRITE)
	if remap == null:
		fail("Cannot write cover remap")
		return
	remap.store_string('[remap]\npath="res://RouteSuggestPlus/mod_image.res"\n')
	remap.close()
	var pack := PCKPacker.new()
	if pack.pck_start(args[1]) != OK:
		fail("Cannot create cover pack")
		return
	for name in ["mod_image.res", "mod_image.png.remap"]:
		if pack.add_file("res://RouteSuggestPlus/" + name, "res://" + name) != OK:
			fail("Cannot pack " + name)
			return
	if pack.flush() != OK or not ProjectSettings.load_resource_pack(args[1]):
		fail("Cannot finish or reload cover pack")
		return
	var path := "res://RouteSuggestPlus/mod_image.png"
	if not ResourceLoader.exists(path):
		fail("Game's cover resource path is missing")
		return
	var loaded := ResourceLoader.load(path) as Texture2D
	if loaded == null or loaded.get_size() != Vector2(source.get_size()):
		fail("Cover did not load as a texture at the original size")
		return
	if loaded.get_image().get_data() != source.get_data():
		fail("Packed cover pixels or alpha differ from the original")
		return
	print("COVER_PASS ", path, " ", loaded.get_size(), " RGBA preserved")
	quit(0)
