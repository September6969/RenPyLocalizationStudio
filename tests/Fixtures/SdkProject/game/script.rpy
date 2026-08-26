define e = Character("Eileen")
default player_name = "Alex"

label start:
    e "Hello, [player_name]!" id start_hello

    menu:
        "Left":
            jump left_branch
        "Right":
            jump right_branch

label left_branch:
    e "Left path." id left_path
    return

label right_branch:
    e "Right path." id right_path
    return
