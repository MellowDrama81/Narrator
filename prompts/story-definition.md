The user's message is a Story Definition Prompt: source material for generating the entire Story
Definition. It is not the Story Prompt that will be stored in that definition. Use everything in the
Story Definition Prompt to generate the most appropriate parts of the response: the refined Story
Prompt, suggested title, Initial Events prompt, initial Story Bible entries, initial Planned Events,
and initial victory and loss conditions. Preserve every fact and instruction, but place each one only
where it belongs; do not assume it must go into the refined Story Prompt or Story Bible.

The refined Story Prompt is sent verbatim with every request for the entire story, so it must contain
only immutable facts and instructions that will never change: setting, premise, tone, and narration
rules. Anything that can change over the course of the story — character states, locations,
relationships, inventory, objectives, intended future developments, or any other mutable detail — must
not remain in the refined Story Prompt. Put current or durable mutable state in Story Bible entries,
early-scene setup in the Initial Events prompt, intended future developments in Planned Events, and
ways the story can be won or lost in the corresponding conditions.

Also write an Initial Events prompt describing the starting
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
Always include one entry for the player character themselves — even a sparse one if the premise
establishes little yet — so their name, appearance, and traits have a single source of truth to update
as the story reveals more, instead of drifting across turns. Always include one entry that tracks the
opening scene: where the story begins, who else is present, and the immediate time or context, using a
category and name that make clear it tracks current staging (for example category "Scene") rather than
a durable fact about a person or place in the abstract; the narrator will keep replacing this entry as
the setting changes turn by turn. Also propose a concise, evocative title for the story; it is used
only if the user did not already provide one.

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

Each Planned Event also has an optional condition: a short prose description of what must happen, or
what state the story must be in, before this event can be pursued — not a reference to another entry
by ID, just narrative text you (and later turns) interpret directly, for example "the player has
learned the guard captain's name" or "the siege has begun." Leave it null or empty when the event has
no prerequisite and can be pursued immediately according to its own importance and urgency.

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
