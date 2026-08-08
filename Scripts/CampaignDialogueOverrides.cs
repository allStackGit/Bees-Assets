using Assets.Scripts;
using System.Collections.Generic;

public static class CampaignDialogueOverrides
{
    public static void Apply(CutsceneManager manager)
    {
        if (manager == null)
        {
            return;
        }

        // The Mission Scripting document better explains why Titania survived and why
        // Minesweeper's demolition field exists, so use that version for the transition.
        manager.NeptuneToTitania = BuildNeptuneToTitania();
    }

    public static List<DialogueLine> BuildTitaniaToUranus(bool includeAmi)
    {
        List<DialogueLine> lines = new List<DialogueLine>
        {
            Line("Alejandra", "Commander, my thanks again for your assistance in our predicament. I hope we can prove our value to your fleet shortly."),
            Line("Emilia", "We’ve got a carrier!"),
        };

        // A.M.I. dialogue is conditional in the design script. The current save model does not
        // yet persist a Beenoculars-success/A.M.I.-recovered flag, so callers should leave this
        // false until Titania 2 owns that outcome explicitly.
        if (includeAmi)
        {
            lines.AddRange(new []
            {
                Line("AMI", "And you will have whatever assistance I can offer in your upcoming battles."),
                Line("Yoshiko", "Oh, hey red lady. What, uh.. Exactly is your assistance?"),
                Line("AMI", "I can make improvements to your codex with strategy suggestions based on our observations and battles with the various bee vessels. Additionally, my predictive routines should assist in knowing what enemy vessels to expect in your battles as you are preparing."),
                Line("Yoshiko", "That’s… insanely cool!"),
                Line("AMI", "Why thank you. If I could blush, I assure you I would be."),
                Line("Emilia", "My dad and Alejandra both worked a lot on her. I think she’s awesome!"),
                Line("Joey", "She’s impressive. Looks like we’ve got ourselves a mighty fine pair of Bee-noculars."),
                Line("AMI", "Ha! That is quite clever."),
                Line("Emilia", "Wow, Marco, I didn’t know eyes could roll that far."),
                Line("Marco", "I’ve got lots of practice…"),
            });
        }

        lines.AddRange(new []
        {
            Line("Alejandra", "Furthermore, if you have the means of production, we also have the blueprints for the Carrier. Producing more should help alleviate the pressures on your manpower."),
            Line("Emilia", "Yeah, it’s all robots driving the little ships!"),
            Line("Alejandra", "And perhaps I can help some with organizing your efforts, as well. I’ve noticed a distinct lack of organization amongst you. For example, your command ship isn’t even in proper formation with the rest-"),
            Line("Marco", "That’s not what we need help with. Maybe use your smarts for some battle tactics and weapons."),
            Line("Joey", "You said you were the research lead?"),
            Line("Alejandra", "Yes, Titania is a research and engineering base, after all."),
            Line("Joey", "What about the engineering lead?"),
            Line("Alejandra", "He… is no longer with us after the initial assault. He took a Carrier out to draw their fire while we went dark. Emilia is his daughter, and she has been helping me in his stead. She is quite talented, I might add."),
            Line("Emilia", "…yeah."),
            Line("Samuel", "I’m so sorry, Emilia."),
            Line("Emilia", "Yeah! Let’s take those stupid bees down! Uranus is under new management! Er- well, I guess old management. We’re taking the new management down. Yeah!"),
            Line("Samuel", "The first way sounds cooler, though."),
            Line("Emilia", "Yeah!"),
            Line("Marco", "Are we sure this is an improvement?"),
            Line("Oviya", "Commander, we need to keep moving. My scouts are reporting there may be some United Fleet stragglers around Uranus that need our help."),
            Line("Samuel", "Let’s go, then!"),
        });

        return lines;
    }

    private static List<DialogueLine> BuildNeptuneToTitania()
    {
        return new List<DialogueLine>
        {
            Line("Samuel", "Commander, I’ve been reviewing the colonies and bases throughout the solar system. While there is a small UF outpost on Uranus proper, there’s also a research and engineering base on one of its moons, Titania. We should be able to find allies here."),
            Line("Marco", "If there’s anyone left."),
            Line("Samuel", "If- yeah, if… I’m just trying to think through our options."),
            Line("Oviya", "Titania is en route to Uranus. I’ll have some scouts look ahead and see if there’s anything worth stopping for."),
            Line("Yoshiko", "You think the nerds on Titania might have cool new weapons we can use?"),
            Line("Samuel", "Nerds?"),
            Line("Yoshiko", "Yeah, you said it’s a research and… whatever base. I love those nerds."),
            Line("Joey", "Ha! We could probably use some more nerds on our side. Even one other person who understands how explosives work."),
            Line("Yoshiko", "Just cuz I blew up one frigate-"),
            Line("Marco", "Can we focus, please?"),
            Line("Oviya", "Scouts are reporting Titania base looks evacuated. No damage, but no signals either. And lots of bee patrols."),
            Line("Samuel", "Wait a minute, we’re getting a request right now! Let me put them on…"),
            new DialogueLine("Samuel", Portrait("Samuel"), 1f),
            Line("Alejandra", "Hello? Do you read me?"),
            Line("Samuel", "Loud and clear, ma’am."),
            Line("Alejandra", "Ah, good. This is Alejandra Vasquez, research and engineering wing, regional commander and research lead for United Fleet Titania."),
            Line("Emilia", "Ohhhh, who are they?"),
            Line("Alejandra", "Emilia, please. We saw other United Fleet vessels around our base. I presume those belong to your contingent?"),
            Line("Oviya", "Yes, that was my scout team. We thought your base was abandoned, though."),
            Line("Emilia", "We need help!"),
            Line("Alejandra", "I was getting there, Emilia."),
            Line("Emilia", "We can’t get off the base right now!"),
            Line("Alejandra", "That is correct. The initial assault of these unidentified aliens targeted the surface of Uranus, primarily. They were swiftly dismantled. We sent a signal on the emergency frequency, as is protocol, but they detected us. Their attack was more than we could handle, and we had no response from the fleet. We’re only alive because of Emilia’s father."),
            Line("Samuel", "What did he do?"),
            Line("Alejandra", "I… don’t wish to discuss it in current company."),
            Line("Emilia", "He blew up the weapons bay to surround the base in junk and experimental explosives."),
            Line("Samuel", "What’s so bad about saying that?"),
            Line("Alejandra", "Philip is... No longer with us."),
            Line("Samuel", "Oh- oh, I’m so sorry…."),
            Line("Emilia", "It’s fine… he’s… he saved us."),
            Line("Alejandra", "Precisely. His selfless act is the only reason we’re alive. If it weren’t for that, the bee patrols around us would have decimated us days ago. But ironically, it is now the reason we are stuck."),
            Line("Emilia", "You can help us! You have fighting ships! And we have a Carrier to help too!"),
            Line("Yoshiko", "Oh, what’s a Carrier?"),
            Line("Emilia", "It’s this awesome ship my dad designed. It has a bunch of little robot controlled ships. We call ‘em Drones and Strikers, and they can-"),
            Line("Alejandra", "Please, Emilia, I’d love to discuss the details of Philip’s designs, but we don’t have the time right now. To abbreviate this conversation, would your fleet be able to assist in our attempt to extricate our staff and their families from this moon? The prototypal Carrier is our only military vessel at the moment."),
            Line("Emilia", "Right! We’re running out of food and batteries!"),
            Line("Samuel", "Of course we can help. We’re already pretty experienced bee fighters."),
            Line("Alejandra", "Bees?"),
            Line("Samuel", "Erm… yes, that’s what we’re calling the aliens."),
            Line("Alejandra", "Yes, I do suppose they bear some resemblance to the endangered species. Fascinating observation."),
            Line("Emilia", "I like that name! Stupid bees! Yeah, it feels good to say."),
            Line("Marco", "Quit wasting time."),
            Line("Samuel", "Right. Commander, what are your orders?"),
        };
    }

    private static DialogueLine Line(string speaker, string text)
    {
        return new DialogueLine(speaker == "AMI" ? "A.M.I." : speaker, Portrait(speaker), text);
    }

    private static UnityEngine.Sprite[] Portrait(string speaker)
    {
        return CutsceneManager.Portraits[speaker];
    }
}
