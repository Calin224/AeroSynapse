import {
  ApplicationConfig,
  isDevMode,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter, withViewTransitions } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { providePrimeNG } from 'primeng/config';
import { routes } from './app.routes';
import { apiErrorInterceptor } from './core/http/api-error.interceptor';
import { responseFeedbackInterceptor } from './core/http/response-feedback';
import { AeroSynapseTheme } from './core/theme/app-theme';
import { authInterceptor } from './core/auth/auth-interceptor';
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withViewTransitions({ skipInitialTransition: true })),
    provideAppInitializer(() => {
      if (!isDevMode() && 'serviceWorker' in navigator)
        void navigator.serviceWorker
          .register(new URL('ngsw-worker.js', document.baseURI), {
            scope: new URL('.', document.baseURI).pathname,
          })
          .catch((error) => console.warn('Offline shell registration unavailable.', error));
    }),
    provideHttpClient(
      withInterceptors([apiErrorInterceptor, authInterceptor, responseFeedbackInterceptor]),
    ),
    providePrimeNG({
      theme: { preset: AeroSynapseTheme, options: { darkModeSelector: '.app-dark' } },
    }),
  ],
};
