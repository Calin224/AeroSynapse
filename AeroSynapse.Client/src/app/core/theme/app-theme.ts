import { definePreset } from '@primeuix/themes';
import Aura from '@primeuix/themes/aura';

// PrimeNG tokens follow the palette declared in src/styles.css (--accent, --panel, --border…),
// so PrimeNG widgets and native controls read as one design system.
export const AeroSynapseTheme = definePreset(Aura, {
  semantic: {
    primary: {
      50: '#edfdf8',
      100: '#d1faee',
      200: '#a6f3de',
      300: '#7ae6cc',
      400: '#59d9ba',
      500: '#2fbf9f',
      600: '#1f9a82',
      700: '#1e7a69',
      800: '#1e6154',
      900: '#1c5046',
      950: '#0a2f29',
    },
    formField: { borderRadius: '8px' },
    colorScheme: {
      dark: {
        surface: {
          0: '#ffffff',
          50: '#f3f6fa',
          100: '#e9eff6',
          200: '#c8d3df',
          300: '#9bacbf',
          400: '#7a8da3',
          500: '#5d7188',
          600: '#44586e',
          700: '#2f4258',
          800: '#213042',
          900: '#172433',
          950: '#0c1520',
        },
      },
    },
  },
  components: {
    button: { root: { borderRadius: '8px', paddingY: '0.55rem' } },
    message: { root: { borderRadius: '10px' } },
  },
});
