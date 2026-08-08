# Campaign design guide

This file records **guiding, non-authoritative campaign intent** from the mission outline supplied during development. It is useful for interpreting unfinished levels and planning future work, but it must not override current runtime code, authored assets, dialogue, or server-backed campaign data when they disagree.

The authoritative implementation sources remain `CampaignMissionCatalog`, `LevelIntro`, mission triggers, current level data, maps/obstacle prefabs, ship mechanics, and dialogue.

## Campaign-wide intent

- The campaign is a persistent war, so failure is often meant to **change later missions rather than force a retry**. Common consequences are skipped missions, harder future battles, lost resources/intelligence/ships, or additional Bees surviving into later large engagements.
- Side objectives and earlier performance are intended to accumulate into later mission difficulty and available information. The final Earth objective is explicitly planned to vary based on earlier side objectives and intelligence gathered.
- Ship unlocks are strategic campaign milestones rather than isolated rewards. Planned milestones include Frigate/Dreadnought, Factory, Carrier, Cruiser, Barge, Flagship, Warp Gate, Fire Barge, and eventually encounters with the Queen.
- Current Uranus mission 3 is only a temporary endpoint. The planned campaign continues through Saturn, Jupiter, Europa, the Asteroid Belt, Mars, Venus/Sun, the Moon, and Earth.

## Pluto

### Anomaly
Guided first-contact mission. Investigate a strange ship approaching a restricted sector, receive orders from high command, and eventually open fire. Heavy dialogue establishes the situation. Guiding roster additions: Human Scout/Gunship; Bee Honeybee. Failure tone is primarily embarrassment rather than a strategic branch.

### Reinforcements
Bee reinforcements arrive and the player fights them while discovering communications with the rest of the fleet have been cut. Guiding Bee introduction: Wasp.

### Pushback
Human forces have gathered enough strength to push back against the assault and establish footing. Guiding Human additions: Frigate and Dreadnought. Guiding Bee introduction: Hornet.

### Bluer Pastures
Delay the Bees long enough to evacuate Pluto, then withdraw toward Neptune. Guiding Bee introductions: Leafcutter and Yellow Jacket. Performance should matter because evacuation and attrition feed the persistent war.

## Neptune

### Seize the Means
Neptune's defenders could not hold their factories/mining positions. The player fights to reclaim industrial capability and find new mining opportunities. Guiding Bee introduction: Carpenter Bee. Failure means the Factory is not obtained and the following mining mission is skipped.

### Of Production!
Use newly available Factory ships to mine a small group of ore-rich asteroids. Duration is player-chosen: staying longer yields more resources but escalating danger creates increasing risk of fleet losses. Guiding Human addition: Factory.

### Pressing Forward
Break the blockade beyond Neptune and progress toward the Titania/Uranus stage of the campaign. The outline's guiding failure consequence is that Bee-noculars is skipped.

## Titania

### Invaluable Time
This is the design-outline identity corresponding to the current runtime mission called **Minesweeper**. Clear Bee patrols around Titania so its systems can restart and evacuation preparations can proceed. Current authored gameplay additionally uses the dense demolition maze and Fire Tanks, so code/assets provide more specific tactical detail than this outline.

Guiding failure consequence: Bee-noculars begins significantly harder and some resources that would have been gained there are automatically lost. This consequence is not authoritative until implemented.

### Bee-noculars
Defend the Titania base while personnel evacuate and Emilia uploads A.M.I. so it can accompany the fleet. Guiding success reward: mission forecasts become available.

## Uranus

### On the Offensive
Attack the Bee fleet/base in Uranus space while introducing additional Human and Bee ship types. Guiding Human introduction: Carrier (including Drone/Striker capability). Guiding Bee introduction: Bumblebee. Success obtains the Cruiser. Failure skips On the Defensive.

### On the Defensive
An amplified version of Of Production: collect resources under escalating reinforcement pressure and decide how long to remain before escaping. Guiding success/progression can unlock the Barge if enough resources are gathered. This mission is skipped if the player does not own a Factory.

### A New Threat
Marge makes contact after fleeing Saturn's military manufacturing base while pursued by Bees. The player must rescue her quickly. Guiding success reward: Barge obtained.

## Saturn — planned

### Lie and Wait
Reconnaissance mission using a small force to observe Bee fleet movements and identify the best time to strike Saturn without being detected. Failure/poor reconnaissance makes the following battle substantially harder.

### Turning Point
Large battle for Saturn. Difficulty depends on Lie and Wait performance. An allied fleet joins with a Flagship. Poor results are intended to leave additional Bees for later major battles.

### It's How Big?!
The Bees deploy an unfinished Queen prototype from the manufacturing plant. The prototype has reduced health because construction is incomplete, but rapidly destroys the allied fleet and leaves the player to finish it. Guiding Bee introduction: Queen. Failure adds an extra Queen to the final battle.

## Jupiter — planned

### Picking up the Pieces
Rescue scattered Human ships being pursued by the Bee fleet. The final rescue is the surviving allied Flagship, which joins the player's fleet. Guiding reward: Flagship.

### Triangulating
Local Human communications have also been jammed, implying a nearby Bee jamming station. Explore Jupiter space to locate it. Failure skips Dropping Bombs and inherits that mission's failure consequences.

### Dropping Bombs
Guide Frigates above the jamming station to deploy purpose-built bombs. A bomb malfunction turns the mission into a defense while the Frigates repair the problem. Failure leaves communications jammed, skips Europa, and gives the player substantially less information during later mission preparation.

## Europa — planned

### For Science!
Defeat the Bee force attacking Europa. Faster completion saves more scientists. Failure skips the following Europa mission.

### Insectnapping
Scientists ask the player to capture a Bee ship and return it to Europa for study. Success primarily unlocks additional lore/intelligence.

## Asteroid Belt — planned

### Reclaim the Spaceway
Destroy the Bee force defending Human bases controlling the Spaceway, an artificial safe corridor through the asteroid belt. Guiding reward: Flagship if it was not obtained during Picking up the Pieces. Failure skips the next mission and leaves additional Bees for later large battles.

### Defend the Spaceway
Hold the reclaimed bases while the Spaceway closes, with Bee reinforcements continuing to arrive until closure.

### Escape to Mars
After the Spaceway closes, the remaining fleet must traverse the asteroid belt to rejoin the main force near Mars while fighting Bee stragglers.

## Mars — planned

### Protect the Capitol
Destroy a Bee orbital-bombardment center before Mars's capital aligns with its strike. The outline describes catastrophic civilian consequences for failure, but the exact gameplay representation of that consequence is deliberately unresolved.

### Unite the Fleet
Mars's heavy military force has been disrupted and divided. Fight the Bees while rescuing as many Mars squads as possible. Guiding Human reward/introduction: experimental Warp Gate.

## Venus / Sun — planned

### Hot Garbage
The Bees are scavenging Venus's junkyard for manufacturing material. If the player has both Barges and the strategy AI capability, the AI proposes converting decommissioned Barges in the junkyard into Fire Barges. Guiding reward: Fire Barge.

### Disrupt the Supply Chain
Destroy Bee defenses and shut down the manufacturing plant to reduce sustained pressure on Earth. Failure leaves significantly more Bees available for the remainder of the campaign.

## Moon — planned

### Float Like a Butterfly...
The Moon is lightly defended because the Bees committed forces elsewhere. Defeat them to gain access to Human ships stationed there. Failure means those additional ships are not gained.

## Earth — planned

### United We Stand
Fight through the chaos in Earth orbit to reunite with Earth's main fleet, only to find it already under severe pressure.

### Divided They'll Fall
Planned final battle. The exact objective changes based on earlier side objectives and intelligence gathered. The war ends here.
