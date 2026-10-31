import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { providePrimeNG } from 'primeng/config';
import { routes } from './app.routes';
import { apiErrorInterceptor } from './core/http/api-error.interceptor';
import { AeroSynapseTheme } from './core/theme/app-theme';
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([apiErrorInterceptor])),
    providePrimeNG({
      theme: { preset: AeroSynapseTheme, options: { darkModeSelector: '.app-dark' } },
    }),
  ],
};
