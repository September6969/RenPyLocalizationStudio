init python hide:
    payload = """
label fake_from_triple:
    jump fake_target
screen fake_screen():
"""

screen preferences_variant(section="language"):
    vbox:
        text "screen text"
        if persistent.flag:
            textbutton "button"

label start:
    narrator happy "Visible: [name]"
    jump expression persistent.destination

label finish:
    return
