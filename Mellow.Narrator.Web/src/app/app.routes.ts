import { Routes } from '@angular/router';
import { DefinitionEditorComponent } from './pages/definition-editor.component';
import { DefinitionsComponent } from './pages/definitions.component';
import { PlayComponent } from './pages/play.component';
import { SettingsComponent } from './pages/settings.component';
import { StoriesComponent } from './pages/stories.component';
import { TrashComponent } from './pages/trash.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'definitions' },
  { path: 'settings', component: SettingsComponent, title: 'Settings · Mellow Narrator' },
  { path: 'definitions', component: DefinitionsComponent, title: 'Story Definitions · Mellow Narrator' },
  { path: 'definitions/:id', component: DefinitionEditorComponent, title: 'Definition · Mellow Narrator' },
  { path: 'stories', component: StoriesComponent, title: 'Stories · Mellow Narrator' },
  { path: 'stories/:id', component: PlayComponent, title: 'Play Story · Mellow Narrator' },
  { path: 'trash', component: TrashComponent, title: 'Trash · Mellow Narrator' },
  { path: '**', redirectTo: 'definitions' },
];
