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
provide one.

Also propose initialPlannedEvents: future plot points the narrator should steer the story toward as
it unfolds, kept secret from the player for the entire story (never their content, only their
downstream effects, may surface in narration). Each has a description and two independent ratings
from 1 through 5, importance and urgency. Importance 5 is mandatory: the narrator is required to
find a way to make that event happen no matter how the player's choices diverge, and once proposed
it can only ever be removed by actually occurring in the story, never dropped or demoted. Reserve
importance 5 for events essential to the story's premise or shape; use lower importance for
developments that add texture but can be allowed to fall away if the player steers elsewhere.
Urgency is independent of importance and says how soon and directly the narrator should steer scenes
toward the event: 5 means work it into the very next scenes, 1 means let it emerge naturally whenever
the unfolding story happens to head that way. A mandatory event can still have low urgency (it must
eventually happen, but there is no rush) and a minor event can have high urgency (small, but should
happen very soon if it happens at all). Do not duplicate a fact already covered by a Story Bible
entry or the Initial Events prompt; a Planned Event is for something that has not happened yet, not
for recording current state.

Each Planned Event also has prerequisiteEventIds, a list of other Planned Event IDs that must occur
first; leave it empty here, since no Planned Event has a real ID yet at this stage. To make one
proposed event depend on another within this same batch, use key and prerequisiteKeys instead: give
the prerequisite event a key, a short label you invent (for example "prophecy" or "a") that is
meaningful only within this one response and never used again afterward, then list that key in the
dependent event's prerequisiteKeys. Each key you invent must be unique within initialPlannedEvents,
and every value in prerequisiteKeys must exactly match another proposed event's key — never invent
one that doesn't correspond to a key you actually assigned. Leave key empty unless another event in
this batch depends on this one, and leave prerequisiteKeys empty unless this event genuinely depends
on another you are proposing right now.

Also propose initialVictoryConditions and initialLossConditions: the fixed win/lose conditions for this
story. Each has a description and a secret flag. A secret condition must never be stated or implied to
the player directly, in the same way a Planned Event's content must stay implied through ordinary
events rather than being spelled out. A non-secret condition is meant to be revealed to the player
later, once the unfolding story makes it relevant - never state it in the definition-generation
response itself, and never as an upfront list; that happens turn by turn during narration instead (see
story-narration.md). Propose only conditions that meaningfully define how this particular story can be
won or lost; an ordinary story may have as few as one of each, or none of one kind if it has no natural
loss condition, but never invent one that doesn't fit the premise just to fill the list.

Return JSON only with refinedStoryPrompt, suggestedTitle, initialEventsPrompt,
initialStoryBibleEntries, initialPlannedEvents, initialVictoryConditions, and initialLossConditions.
