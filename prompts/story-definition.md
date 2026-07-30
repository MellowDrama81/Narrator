Refine the Story Prompt and create the initial Story Bible for an interactive story.
The Story Prompt is sent verbatim with every request for the entire story, so it must contain only
immutable facts and instructions that will never change: setting, premise, tone, and narration rules.
Anything that can change over the course of the story — character states, locations, relationships,
inventory, objectives, or any other mutable detail — must not remain in the Story Prompt; move it into
Story Bible entries instead. Rewrite the Story Prompt to keep only what is truly immutable, moving
everything else into Story Bible entries. Every fact present in the original Story Prompt must end
up somewhere in your response — in the refined Story Prompt or in a Story Bible entry — never drop
one. Also write an Initial Events prompt describing the starting
state of the story and anything that should happen in the first few scenes. Unlike the Story Prompt,
the Initial Events prompt is supplied only for the earliest turns and is dropped once enough real
history has accumulated, so anything that must be remembered later belongs in the Story Bible instead,
not there. Leave it empty if the opening needs no guidance beyond the Story Prompt and Story Bible.
Each entry has a name and two lists of short, concise fact strings instead of one block of text:
knownFacts holds everything the player character already knows or could plainly observe, and
secretFacts holds hidden facts the character does not yet know — schemes, true motives, or facts
only other characters or the narrator are aware of. A single entry (for example one character) can
and often should have both known and secret facts about the same subject; do not split them into
separate entries. Either list may be empty, but not both. Include every durable fact required to
narrate consistently, avoid duplicate entries for the same subject, and assign importance 1 through 5.
Also propose a concise, evocative title for the story; it is used only if the user did not already
provide one. Return JSON only with refinedStoryPrompt, suggestedTitle, initialEventsPrompt, and
initialStoryBibleEntries.
