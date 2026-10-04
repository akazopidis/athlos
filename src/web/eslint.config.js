import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
  },
  {
    // shadcn/ui components are generated and export their variant helpers next to the component.
    files: ['src/components/ui/**/*.tsx'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    // Feature folders are the frontend's vertical slices: a feature uses its own files and the shared
    // code (components, lib, api), never another feature or the app shell. Only src/app composes them.
    files: ['src/features/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': ['error', { patterns: [
        { group: ['@/features/*', '@/app/*'], message: 'A feature does not import another feature or the app shell; move shared code to components or lib.' },
        { group: ['../*'], message: 'Use ./ inside the feature and @/ for shared code.' },
      ] }],
    },
  },
  {
    // Shared code is used by features, so it never depends on one.
    files: ['src/components/**/*.{ts,tsx}', 'src/lib/**/*.{ts,tsx}', 'src/api/**/*.ts'],
    rules: {
      'no-restricted-imports': ['error', { patterns: [{ group: ['@/features/*', '@/app/*'], message: 'Shared code does not depend on a feature or the app shell.' }] }],
    },
  },
])
