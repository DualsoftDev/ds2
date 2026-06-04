---
name: Industrial Insight System
colors:
  surface: '#f7f9fb'
  surface-dim: '#d8dadc'
  surface-bright: '#f7f9fb'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f2f4f6'
  surface-container: '#eceef0'
  surface-container-high: '#e6e8ea'
  surface-container-highest: '#e0e3e5'
  on-surface: '#191c1e'
  on-surface-variant: '#45474c'
  inverse-surface: '#2d3133'
  inverse-on-surface: '#eff1f3'
  outline: '#75777d'
  outline-variant: '#c5c6cd'
  surface-tint: '#545f73'
  primary: '#091426'
  on-primary: '#ffffff'
  primary-container: '#1e293b'
  on-primary-container: '#8590a6'
  inverse-primary: '#bcc7de'
  secondary: '#0058be'
  on-secondary: '#ffffff'
  secondary-container: '#2170e4'
  on-secondary-container: '#fefcff'
  tertiary: '#1e1200'
  on-tertiary: '#ffffff'
  tertiary-container: '#35260c'
  on-tertiary-container: '#a38c6a'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#d8e3fb'
  primary-fixed-dim: '#bcc7de'
  on-primary-fixed: '#111c2d'
  on-primary-fixed-variant: '#3c475a'
  secondary-fixed: '#d8e2ff'
  secondary-fixed-dim: '#adc6ff'
  on-secondary-fixed: '#001a42'
  on-secondary-fixed-variant: '#004395'
  tertiary-fixed: '#fadfb8'
  tertiary-fixed-dim: '#ddc39d'
  on-tertiary-fixed: '#271902'
  on-tertiary-fixed-variant: '#564427'
  background: '#f7f9fb'
  on-background: '#191c1e'
  surface-variant: '#e0e3e5'
typography:
  display-metrics:
    fontFamily: Inter
    fontSize: 32px
    fontWeight: '700'
    lineHeight: 40px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
  headline-md:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 24px
  body-lg:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 24px
  body-md:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
  label-sm:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 16px
    letterSpacing: 0.02em
  mono-data:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '600'
    lineHeight: 20px
rounded:
  sm: 0.125rem
  DEFAULT: 0.25rem
  md: 0.375rem
  lg: 0.5rem
  xl: 0.75rem
  full: 9999px
spacing:
  container-margin: 24px
  gutter: 16px
  card-padding: 20px
  stack-sm: 8px
  stack-md: 16px
---

## Brand & Style

The design system is engineered for precision, reliability, and high-velocity decision-making in industrial environments. It adopts a **Corporate Modern** aesthetic with a focus on data density without visual fatigue. 

The brand personality is authoritative yet unobtrusive, prioritizing the clarity of operational metrics over decorative elements. By utilizing a structured hierarchy and a neutral "canvas" (light grays and whites) contrasted against a high-utility sidebar, the system ensures that critical alerts (Red/Amber) command immediate attention. The overall emotional response is one of controlled efficiency and professional oversight.

## Colors

The color palette is strategically divided into functional zones:

- **Navigation Zone:** Uses a deep navy/slate (`#0F172A`) to provide a strong grounding element and clear separation from the workspace.
- **Surface Zone:** Employs a clean, high-brightness palette (`#F8FAFC` for backgrounds and white for cards) to maximize readability of complex charts.
- **Semantic Zone:** Strictly reserved for status signaling. **Green** indicates active/optimized states, **Amber** represents waiting or idle states, and **Red** signals errors or critical cycle-time thresholds.
- **Accents:** A professional blue is used for primary actions and interactive elements (buttons, active tabs).

## Typography

This design system utilizes **Inter** for its exceptional legibility at small sizes and its "tabular num" (tnum) features, which are essential for aligning numerical data in monitoring tables and dashboards.

- **Data-First:** Large display weights are used for primary KPIs to allow for "glanceable" monitoring from a distance.
- **Hierarchy:** Use bold weights for section headers and semi-bold for secondary labels to create a clear structural scan-path.
- **Numerical Precision:** Always enable tabular lining for numbers in data tables to ensure columns align perfectly regardless of the specific digits shown.

## Layout & Spacing

The layout follows a **Fixed-Fluid Hybrid** model. The sidebar remains at a fixed width (240px), while the main content area utilizes a 12-column fluid grid.

- **Grid Logic:** Complex monitoring views use a 12-column grid. Top-level KPIs typically span 3 or 4 columns. Main visualization areas (Floor Layout) span 8-9 columns, with auxiliary charts (CT Comparison) occupying the remaining 3-4 columns.
- **Breakpoints:** 
  - **Desktop (1440px+):** Full 12-column visibility.
  - **Tablet (768px - 1439px):** Content reflows to a single column for the floor layout, with charts stacking vertically.
  - **Mobile:** Sidebar collapses into a hamburger menu; all cards stack vertically with reduced `container-margin` (16px).
- **Density:** Spacing is kept tight (8px/16px increments) to allow maximum data visibility on a single screen without requiring scrolling.

## Elevation & Depth

Visual hierarchy is established through **Tonal Layers** and **Low-Contrast Outlines**.

- **Level 0 (Background):** The base application surface uses a subtle off-white/gray.
- **Level 1 (Cards):** All primary content containers are pure white with a 1px border (`#E2E8F0`) and a very soft, diffused shadow (Blur: 4px, Y: 2px, Opacity: 0.05).
- **Interactive States:** Hovering over an interactive card or list item increases the shadow depth and adds a subtle primary-colored left border.
- **Modals & Overlays:** Use a standard backdrop blur (8px) and a higher elevation shadow to separate system-critical dialogues from the monitoring data.

## Shapes

The design system uses a **Soft** shape language (`0.25rem` base radius). This maintains a professional, industrial feel that is more approachable than sharp corners but more structured than "pill" or "bubbly" consumer designs.

- **Input Fields/Buttons:** Use the standard `rounded` (4px).
- **Status Badges/Chips:** Use `rounded-lg` (8px) to distinguish them as distinct labels.
- **Main Containers/Cards:** Use `rounded-lg` (8px) for a subtle, modern framing of content.

## Components

### Cards
Cards are the primary structural unit. Every card must feature a header row with a title (`headline-md`) and an optional "actions" area (e.g., settings icon or expand button). Card backgrounds for status-critical metrics may use a very light tinted background (e.g., 5% opacity Red for error states).

### Progress Bars & Gauges
Used for showing cycle time (CT) progress. Use a gray background track with a colored bar representing the current state (Success/Warning/Error). Labels should be placed at either end of the bar for context.

### Status Badges
Small, pill-shaped indicators. Use high-contrast text on a low-saturation background of the same hue (e.g., Dark Green text on Light Green background) to indicate "Ready", "Active", or "Error".

### Sidebar Navigation
The sidebar should use high-contrast white text/icons against the slate background. Active states are indicated by a high-visibility blue bar on the left edge and a subtle background highlight.

### Data Tables
Dense but readable. Use `mono-data` for all numeric values. Header rows should be pinned during scroll and have a slightly darker gray background (`#F1F5F9`).

### Input Fields
Clean, outlined fields. On focus, the border shifts to the primary blue with a 2px outer glow. Labels are always positioned above the input.