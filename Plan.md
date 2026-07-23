# Title
Mellow Narrator

# Description
A .Net MAUI application which allows the user to define and play LLM-driven interactive stories.
The application will connect to an OpenAI-compatible API to use the LLM.

# Solution Structure
Mellow.Narrator: VisualStudio solution
- Mellow.Narrator.Gui: C# .Net 10 MAUI application targeting Windows and Android
- Mellow.Narrator.Core: C# .Net 10 Class Library
- Mellow.Narrator.Cli: C# .Net 10 Console Application
- Mellow.Narrator.Tests: C# .Net 10 Unit Test project

Mellow.Narrator.Gui should contain UI code only.

Mellow.Narrator.Core should contain all code which implements the actual logic of the application.

Mellow.Narrator.Cli is for manually testing code in Mellow.Narrator.Core from the console.

Mellow.Narrator.Tests contains unit tests for Mellow.Narrator.Core

# User Interface

This will be tab-based with 5 different types of tab.
Allow the tabs to be dragged to reorder. Allow the UI to switch between vertical and horizontal tabs.
The open tabs and their order will be persisted so they can be restored after the application is closed and reopened.
Provide some way to create a new Play Story Page by importing a story state JSON file.

## Settings Page

Always exactly 1 tab. This is locked as the 1st (top/leftmost) tab.
Allow the user to configure a connection to an OpenAI-compatible API.
With an API connected, load a list of available models and allow the user to select one.
Allow applicable parameters for the model to be set.
Persist the configuration.

## Story Definition List Page

Always exactly 1 tab. This is locked as the 2nd tab.
A list of Story Definitions.
Allow the user to re-order Story Definitions.
Allow the user to select a Story Definition.
Allow the user to view the selected Story Definition in a new tab (Story Definition Page).
Allow the user to edit the selected Story Definition's Prompt in a new tab (Story Prompt Page).
Allow the user to delete the selected Story Definition.
Allow the user to start a new story using the selected Story Definition (Start Story Page).
Allow the user to export a copy of the selected Story Definition as a JSON file.
Allow the user to import a JSON file containing a Story Definition to add it to the list.

## Story Definition Pages

Multiple copies of this page may be open symultaneously as tabs.
A read-only view of the Title, Story Prompt and player questions.
Allow the user to edit the Story Definition's Prompt (replace this tab with a Story Prompt Page).
Allow the user to start a new story using this Story Definition (replace this tab with a Start Story Page).

## Story Prompt Pages

Multiple copies of this page may be open symultaneously as tabs.
Allow the user to enter a title for the story.
Allow the user to enter a prompt which will be used as the seed for a story.
Allow the user to enter a list of questions which the player must answer before playing the story along with simple validation rules. For example "What is your name?" with the validation "Should not be a girls' name." or "How old are you?" with the validation "Must be at least 18 years old."
Persist the the details entered. Delete them if the corresponding tab is closed.
Provide a button which will allow the user to generate a populated Story Definition. If this tab was opened to edit an existing Story Definition then give the option to overwrite it or create a new one. Replace the tab with a Story Definition page.

## Start Story Pages

Multiple copies of this page may be open symultaneously as tabs.
Collect and validate the user's answers to the questions.
Once all questions are answered, build the initial Story Bible and replace this tab with a Play Story Page.

## Play Story Pages

Multiple copies of this page may be open symultaneously as tabs.
Displays the story narration based on recent narration, player action and the current Story Bible.
Provides suggested actions for the player to choose from.
Provides a text box for the player to enter any action they want (if they don't like the suggestions).
After the player selects or enters an action, the next scene is narrated and appended to the narration and the Story Bible is updated.
Persist the state of the story (recent narration and actions and the Story Bible) so that it can be restored after the application is closed and reopened.
Provide a button to export the current state of the story as a JSON file.