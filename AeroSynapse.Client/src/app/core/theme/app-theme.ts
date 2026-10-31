import { definePreset } from '@primeuix/themes';
import Aura from '@primeuix/themes/aura';

// PrimeNG shares the native controls' monochrome palette and square geometry.
export const AeroSynapseTheme = definePreset(Aura, {
  primitive: {
    borderRadius: { none: '0', xs: '0', sm: '0', md: '0', lg: '0', xl: '0' },
  },
  semantic: {
    transitionDuration: '0s',
    focusRing: { width: '3px', style: 'solid', color: '#000000', offset: '3px' },
    formField: { borderRadius: '0', focusBorderColor: '#000000' },
    colorScheme: {
      light: {
        primary: {
          color: '#000000',
          contrastColor: '#ffffff',
          hoverColor: '#333333',
          activeColor: '#555555',
        },
        highlight: {
          background: '#000000',
          focusBackground: '#333333',
          color: '#ffffff',
          focusColor: '#ffffff',
        },
        surface: {
          0: '#ffffff',
          50: '#f0f0f0',
          100: '#e6e6e6',
          200: '#d4d4d4',
          300: '#bfbfbf',
          400: '#999999',
          500: '#777777',
          600: '#555555',
          700: '#444444',
          800: '#333333',
          900: '#111111',
          950: '#000000',
        },
      },
    },
  },
  components: {
    button: { root: { borderRadius: '0', paddingY: '0.75rem' } },
    message: { root: { borderRadius: '0' } },
    dialog: { root: { borderRadius: '0', shadow: 'none' } },
  },
});
