import { bootstrapApplication } from '@angular/platform-browser';
import { LiveConsoleComponent } from './app/live-console';

bootstrapApplication(LiveConsoleComponent).catch((error) =>
    console.error('Console bootstrap failed', error),
);
