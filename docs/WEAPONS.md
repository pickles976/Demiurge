Take a look at the new sks.gltf model in assets. In addition to the grip and barrel anchors, we also added rear_sight and front_sight
anchors. If you center the model such that you can draw a straight line from the front sight to the rear sight and into the center of
the camera, it will look to the player like they are properly "aiming down sights". Add a pickup and equipped weapon for the SKS, and
make the SKS the default starting weapon for players and NPCs, then update the ADS code to account for our changes to the sights system
(currently the anchors only exist on this weapon). Also, we have a bolt that needs to travel back for 1 or 2 frames during the animation. There is a group named "bolt" and this group should move back from the anchor `bolt_start` (its default position) to `bolt_end`.

The SKS should hold 10 rounds, do as much damage as the AK and have the same ballistics, but should fire semi-auto (requires one click per shot) with a maxmium fire rate of 10 rounds per second.

shovel.gltf has also been added. It needs a simple 1-2 frame swinging animation when digging, like in Minecraft. 

When the main weapon is not equipped, make the character wear it on the back. The shovel should be worn on the left hip when not equipped, facing down towards the ground. There is also grenade.gltf that is ready to be integrated.