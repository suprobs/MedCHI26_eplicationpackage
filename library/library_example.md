# Expert reference library — example (printers & single-serve coffee machines)

> Curated by the expert from a corpus of device manuals. Same table formats as
> Prompts 1–3 (Block 2). This file is an EXAMPLE seed: extend each table as more
> manuals are analyzed, and record the curation criteria in the thesis appendix.

## Components

| Element | Category | Name variants | Type of movement | Typical location |
|---|---|---|---|---|
| Scanner lid | cover | lid, scanner cover, document cover, top cover | hinge | top of the device |
| Scanner glass | surface | flatbed glass, platen, exposure glass | static | under the scanner lid |
| Water tank (reservoir) | container | water container, water reservoir, tank | hinge | rear or side of the machine |
| Brewing lever | lever | lever, brew handle, capsule lever, locking handle | hinge | top of the machine |
| Capsule | consumable | pod, coffee capsule, cápsula | insert | inserted under the lever |
| Cup | consumable | mug, taza, glass | place | on the drip tray, under the outlet |
| Drip tray | tray | drip grid, cup support, drip base | slide | base of the machine, front |
| Power button | control | on/off button, power switch, start button | press | front or top panel |
| Espresso button | control | small cup button, short coffee button | press | top panel |
| Lungo button | control | large cup button, long coffee button | press | top panel |
| Scan button | control | start scan button, copy button | press | control panel |
| Control panel display | display | screen, LCD, status display | static | front panel |
| Paper tray | tray | input tray, paper feed tray, output tray | slide | front or rear |
| Ink cartridge | consumable | cartridge, print cartridge, ink tank | insert | carriage, under the access door |
| Coffee outlet | spout | coffee spout, dispensing nozzle | static | above the cup |
| Power cord | cable | mains cable, power cable | plug | rear |

## Procedures

| Procedure | Brief description | Name variants |
|---|---|---|
| Scan a document | Place a document on the glass and scan it to the computer | scanning, scan to computer, digitalizar documento |
| Prepare an espresso | Insert a capsule and brew a short coffee | make espresso, brew espresso, preparar café |
| Prepare a lungo | Insert a capsule and brew a long coffee | make lungo, brew lungo |
| Fill the water tank | Remove, fill and reseat the water reservoir | refill water, fill reservoir |
| Load paper | Place paper in the input tray | insert paper, refill paper |
| Replace the ink cartridge | Open the access door and swap the cartridge | change cartridge, install cartridge |
| Descale the machine | Run the descaling program with descaling agent | descaling, decalcify |

## Errors

| Error / fault | Category | Name variants |
|---|---|---|
| Paper jam | mechanical | jammed paper, atasco de papel |
| Out of paper | supplies | no paper, paper empty |
| Low ink | supplies | ink low, replace ink soon |
| Empty water tank | supplies | no water, water tank empty |
| Capsule jam | mechanical | stuck capsule, capsule not ejected |
| Machine needs descaling | maintenance | descaling alarm, calc warning |

## Action archetypes (cross-device reuse)

| Archetype | Action verbs | Component keywords | Movement | Core script | Notes |
|---|---|---|---|---|---|
| hinged_cover_open | open, lift, raise, flip, close, lower | lid, cover, lever, handle, door, tank | hinge | ConstrainedMover (FlipHinge) | printer scanner lid ≈ coffee machine lever ≈ water tank on hinge |
| sliding_tray | pull, slide, extend, push in | tray, drawer, drip, paper feed | hinge | ConstrainedMover (Slide via trajectory) | grabbable; the actual slide axis comes from the trajectory panel |
| press_control | press, push, tap, hold | button, control, switch, key | press | EfsmPressTrigger | any momentary control |
| insert_consumable | insert, place, load, put, remove | capsule, pod, cup, mug, paper, cartridge, sheet | press | EfsmStickToTarget | free-floating start, attaches at end pose to its receptacle |
| plug_cable | plug, connect, unplug | cord, cable, plug, connector | press | EfsmStickToTarget | treated as insert-to-socket |
