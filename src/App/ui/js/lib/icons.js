// OptiCore icon set: 24x24, 1.8px stroke, drawn for this app.
import { html } from './core.js';

const p = (d) => `<path d="${d}"/>`;

const I = {
  dashboard: '<rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="5" rx="2"/><rect x="13" y="10" width="8" height="11" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/>',
  bulb: p('M9 18h6M10 21h4M12 3a6 6 0 0 0-3.6 10.8c.6.5 1 1.2 1.1 2.2h5c.1-1 .5-1.7 1.1-2.2A6 6 0 0 0 12 3z'),
  monitor: '<rect x="3" y="4" width="18" height="12" rx="2"/>' + p('M8 20h8M12 16v4'),
  sparkles: p('M11 3l1.9 4.9L18 9.8l-5.1 1.9L11 16.6 9.1 11.7 4 9.8l5.1-1.9zM18.5 15l.9 2.1 2.1.9-2.1.9-.9 2.1-.9-2.1-2.1-.9 2.1-.9z'),
  power: p('M12 3v8M6.4 6.6a8 8 0 1 0 11.2 0'),
  activity: p('M3 12h4l3-8 4 16 3-8h4'),
  gamepad: p('M7.5 8h9a5 5 0 0 1 5 5v.5a3.5 3.5 0 0 1-6.2 2.2L14 14h-4l-1.3 1.7A3.5 3.5 0 0 1 2.5 13.5V13a5 5 0 0 1 5-5zM7 11v3M5.5 12.5h3M16 11.6h.01M18 13.4h.01'),
  sliders: p('M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1') + '<circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
  package: p('M12 3l8 4.5v9L12 21l-8-4.5v-9zM4 7.5l8 4.5 8-4.5M12 12v9'),
  plug: p('M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4'),
  cpu: '<rect x="6" y="6" width="12" height="12" rx="2"/><rect x="9.5" y="9.5" width="5" height="5" rx="1"/>' + p('M9 3v3M15 3v3M9 18v3M15 18v3M3 9h3M3 15h3M18 9h3M18 15h3'),
  wifi: p('M2.5 9a14 14 0 0 1 19 0M5.5 12.5a9.5 9.5 0 0 1 13 0M8.8 16a5 5 0 0 1 6.4 0M12 19.5h.01'),
  shield: p('M12 3l8 3v6c0 4.6-3.3 8-8 9-4.7-1-8-4.4-8-9V6z'),
  shieldCheck: p('M12 3l8 3v6c0 4.6-3.3 8-8 9-4.7-1-8-4.4-8-9V6zM8.8 12l2.3 2.3 4.2-4.6'),
  shieldAlert: p('M12 3l8 3v6c0 4.6-3.3 8-8 9-4.7-1-8-4.4-8-9V6zM12 8.2v4.3M12 15.6h.01'),
  disk: '<ellipse cx="12" cy="6" rx="8" ry="3"/>' + p('M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3'),
  repeat: p('M4 11V9a3 3 0 0 1 3-3h12M16 3l3 3-3 3M20 13v2a3 3 0 0 1-3 3H5M8 21l-3-3 3-3'),
  history: p('M3.5 12a8.5 8.5 0 1 0 2.6-6.1L3.5 8.5M3.5 4v4.5H8M12 8v4.5l3 1.8'),
  gauge: p('M4.2 17a8.5 8.5 0 1 1 15.6 0M12 14l4-5') + '<circle cx="12" cy="14" r="1.4"/>',
  heart: p('M20.5 8.6c0 5-8.5 10.9-8.5 10.9S3.5 13.6 3.5 8.6a4.4 4.4 0 0 1 8.5-1.7 4.4 4.4 0 0 1 8.5 1.7z'),
  wrench: p('M21 7.5a5 5 0 0 1-6.6 4.7L6.5 20.1a1.9 1.9 0 0 1-2.6-2.6l7.9-7.9A5 5 0 0 1 16.5 3l-3 3 .7 2.8 2.8.7z'),
  cog: '<circle cx="12" cy="12" r="3.2"/><circle cx="12" cy="12" r="6.8"/>' + p('M12 2.8v2.4M12 18.8v2.4M21.2 12h-2.4M5.2 12H2.8M18.5 5.5l-1.7 1.7M7.2 16.8l-1.7 1.7M18.5 18.5l-1.7-1.7M7.2 7.2L5.5 5.5'),
  search: '<circle cx="11" cy="11" r="6.5"/>' + p('M20 20l-4.4-4.4'),
  bell: p('M6 16.5V11a6 6 0 1 1 12 0v5.5l1.8 2H4.2zM10 20.5a2 2 0 0 0 4 0'),
  sun: '<circle cx="12" cy="12" r="4"/>' + p('M12 2.5v2.5M12 19v2.5M2.5 12H5M19 12h2.5M5.3 5.3l1.8 1.8M16.9 16.9l1.8 1.8M5.3 18.7l1.8-1.8M16.9 7.1l1.8-1.8'),
  moon: p('M20 14.2A8 8 0 1 1 9.8 4a6.4 6.4 0 0 0 10.2 10.2z'),
  globe: '<circle cx="12" cy="12" r="9"/>' + p('M3 12h18M12 3c2.8 2.5 4 5.5 4 9s-1.2 6.5-4 9c-2.8-2.5-4-5.5-4-9s1.2-6.5 4-9z'),
  x: p('M6 6l12 12M18 6L6 18'),
  check: p('M5 12.5l4.5 4.5L19 7.5'),
  plus: p('M12 5v14M5 12h14'),
  minus: p('M5 12h14'),
  chevR: p('M9 5l7 7-7 7'),
  chevD: p('M5 9l7 7 7-7'),
  chevL: p('M15 5l-7 7 7 7'),
  chevU: p('M5 15l7-7 7 7'),
  alert: p('M12 4L2.8 20h18.4zM12 10v4.5M12 17.3h.01'),
  info: '<circle cx="12" cy="12" r="9"/>' + p('M12 11v5.5M12 7.7h.01'),
  external: p('M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5'),
  refresh: p('M20 11a8 8 0 0 0-14.2-4.2L4 8.8M4 4v4.8h4.8M4 13a8 8 0 0 0 14.2 4.2l1.8-2M20 20v-4.8h-4.8'),
  trash: p('M4 7h16M9.5 7V4.5h5V7M6.5 7l.9 12.5h9.2L17.5 7M10 11v5M14 11v5'),
  play: p('M7 4.5l12 7.5-12 7.5z'),
  stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
  pause: p('M8 5v14M16 5v14'),
  download: p('M12 4v11M7.5 10.5L12 15l4.5-4.5M5 19.5h14'),
  upload: p('M12 15V4M7.5 8.5L12 4l4.5 4.5M5 19.5h14'),
  lock: '<rect x="5" y="11" width="14" height="9" rx="2"/>' + p('M8 11V8a4 4 0 0 1 8 0v3'),
  eye: p('M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z') + '<circle cx="12" cy="12" r="2.8"/>',
  eyeOff: p('M4 4l16 16M10 6a9 9 0 0 1 2-.2c6 0 9.5 6.2 9.5 6.2a14 14 0 0 1-2.7 3.4M6.5 7.5A14 14 0 0 0 2.5 12S6 18.2 12 18.2a9 9 0 0 0 3.6-.8'),
  folder: p('M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z'),
  file: p('M6 3h8l5 5v13H6zM14 3v5h5'),
  battery: '<rect x="2.5" y="7.5" width="16" height="9" rx="2"/>' + p('M21.5 10.5v3M6 10.5v3M9.5 10.5v3'),
  thermo: p('M12 3a2.2 2.2 0 0 0-2.2 2.2v8.6a4.2 4.2 0 1 0 4.4 0V5.2A2.2 2.2 0 0 0 12 3zM12 9v6.5'),
  bolt: p('M13 2.5L4.5 13.5H11l-1 8 8.5-11H12z'),
  user: '<circle cx="12" cy="8" r="4"/>' + p('M4 20.5c1-4 4-5.5 8-5.5s7 1.5 8 5.5'),
  star: p('M12 3.5l2.6 5.4 5.9.8-4.3 4.1 1 5.8-5.2-2.8-5.2 2.8 1-5.8-4.3-4.1 5.9-.8z'),
  filter: p('M4 5h16l-6.2 7.2V19l-3.6-1.8v-5z'),
  more: '<circle cx="5.5" cy="12" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="18.5" cy="12" r="1.3"/>',
  copy: '<rect x="8.5" y="8.5" width="11" height="11" rx="2"/>' + p('M5.5 15.5h-1a1 1 0 0 1-1-1v-10a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1'),
  bug: p('M8.5 7.5a3.5 3.5 0 0 1 7 0M7 11a5 5 0 0 1 10 0v4a5 5 0 0 1-10 0zM12 11v9M3.5 13H7M17 13h3.5M4.5 7.5L7.5 10M19.5 7.5l-3 2.5M4.5 19.5l3-2.5M19.5 19.5l-3-2.5'),
  ram: '<rect x="2.5" y="7" width="19" height="9" rx="1.5"/>' + p('M6.5 10v3M10.2 10v3M13.8 10v3M17.5 10v3M5 16v2.5M9 16v2.5M15 16v2.5M19 16v2.5'),
  gpu: '<rect x="2.5" y="6.5" width="19" height="10" rx="2"/><circle cx="8.5" cy="11.5" r="2.6"/><circle cx="16" cy="11.5" r="2.6"/>' + p('M5 16.5v3M9 16.5v3'),
  ethernet: '<rect x="4" y="4" width="16" height="13" rx="2"/>' + p('M8 17v3h8v-3M8 8v4M12 8v4M16 8v4'),
  rocket: p('M12 15l-3-3c.6-4.3 3.3-8 8.8-8.8.3 5.5-1.5 8.2-5.8 8.8zM9 12l-3.5.5L4 15l3 .5M12 15l-.5 3.5L9 20l-.5-3M15.3 8.7h.01'),
  undo: p('M4 9h10.5a5.5 5.5 0 0 1 0 11H8M4 9l4.5-4.5M4 9l4.5 4.5'),
  key: '<circle cx="8" cy="15" r="4"/>' + p('M11 12l8.5-8.5M16 6.5l2.5 2.5M13.5 9l2 2'),
  camera: p('M4 8.5h3l1.5-2.5h7L17 8.5h3a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1z') + '<circle cx="12" cy="13.5" r="3.4"/>',
  mic: '<rect x="9" y="3" width="6" height="11" rx="3"/>' + p('M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v3'),
  pin: p('M12 21s7-6.2 7-11.5a7 7 0 0 0-14 0C5 14.8 12 21 12 21z') + '<circle cx="12" cy="9.5" r="2.5"/>',
  clock: '<circle cx="12" cy="12" r="9"/>' + p('M12 7v5.2l3.3 2'),
  calendar: '<rect x="3.5" y="5" width="17" height="15.5" rx="2"/>' + p('M3.5 10h17M8 3v4M16 3v4'),
  layers: p('M12 4l9 4.6-9 4.6-9-4.6zM3 12.8l9 4.6 9-4.6M3 17l9 4.6 9-4.6'),
  target: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="5"/><circle cx="12" cy="12" r="1.3"/>',
  flag: p('M5 21V4M5 4.5h11l-1.8 4 1.8 4H5'),
  wand: p('M4 20L15.5 8.5M14 7l3 3M18 3v3.5M21.2 6.3h-3.5M8.5 4v2.5M9.8 5.2H7.2M19 13v2.5M20.2 14.2h-2.5'),
  terminal: '<rect x="3" y="4.5" width="18" height="15" rx="2"/>' + p('M7 9.5l3 2.5-3 2.5M12.5 15h4.5'),
  menu: p('M4 6.5h16M4 12h16M4 17.5h16'),
  arrowUp: p('M12 19V5M5.5 11.5L12 5l6.5 6.5'),
  arrowDown: p('M12 5v14M5.5 12.5L12 19l6.5-6.5'),
  windows: p('M3.5 5.5l7-1v7h-7zM12 4.3l8.5-1.2v8.4H12zM3.5 12.8h7v7l-7-1zM12 12.8h8.5v8.4L12 20z'),
  flame: p('M12 21c3.9 0 6.5-2.6 6.5-6.2 0-3-1.8-4.6-3-6.3-.6 1.5-1.4 2.2-2.3 2.5.3-3-1-5.7-3.7-7.5.3 2.9-1 4.6-2.2 6.2-1 1.4-1.8 2.9-1.8 5 0 3.7 2.6 6.3 6.5 6.3z'),
  leaf: p('M5 19c0-8 5-13.5 15-14.5.5 9.5-4.5 15-12 15-1.2 0-2.2-.2-3-.5zM5 19c2-4.5 5-7.5 9.5-9.5'),
  puzzle: '<rect x="4" y="9" width="11" height="11" rx="2"/>' + p('M9.5 9V6a2 2 0 0 1 4 0v3M15 13.5h3a2 2 0 0 1 0 4h-3'),
  flask: p('M9.5 3.5h5M10.5 3.5v6L5 19a1.3 1.3 0 0 0 1.1 2h11.8A1.3 1.3 0 0 0 19 19l-5.5-9.5v-6M7.5 15h9'),
  link: p('M10 14a4.5 4.5 0 0 0 6.4 0l3-3a4.5 4.5 0 0 0-6.4-6.4l-1.2 1.2M14 10a4.5 4.5 0 0 0-6.4 0l-3 3a4.5 4.5 0 0 0 6.4 6.4l1.2-1.2'),
  chart: p('M4 20V4M4 20h16M8 16v-5M12 16V8M16 16v-7'),
  trend: p('M3 17l6-6 4 4 8-8M15 7h6v6'),
  scale: p('M12 3v18M7 21h10M5 7h14M5 7l-2.5 6a2.8 2.8 0 0 0 5 0zM19 7l-2.5 6a2.8 2.8 0 0 0 5 0z'),
  scissors: '<circle cx="6" cy="6.5" r="2.5"/><circle cx="6" cy="17.5" r="2.5"/>' + p('M8 8l12 9.5M8 16L20 6.5'),
  list: p('M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01'),
  cursor: p('M5 3.5l13 6.5-5.5 2-2 5.5z'),
  hourglass: p('M6.5 3.5h11M6.5 20.5h11M7.5 3.5c0 4.5 3 5.5 4.5 8.5-1.5 3-4.5 4-4.5 8.5M16.5 3.5c0 4.5-3 5.5-4.5 8.5 1.5 3 4.5 4 4.5 8.5'),
  dot: '<circle cx="12" cy="12" r="3"/>',
  grip: '<circle cx="9" cy="6" r="1.2"/><circle cx="15" cy="6" r="1.2"/><circle cx="9" cy="12" r="1.2"/><circle cx="15" cy="12" r="1.2"/><circle cx="9" cy="18" r="1.2"/><circle cx="15" cy="18" r="1.2"/>',
};

export const iconNames = Object.keys(I);

export function Icon({ name, size, class: cls }) {
  return html`<svg class=${cls} width=${size} height=${size} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
    stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" dangerouslySetInnerHTML=${{ __html: I[name] || I.dot }}></svg>`;
}

/** The OptiCore mark: a reactor core. */
export function Logo({ size = 30, class: cls }) {
  return html`<svg class=${cls} width=${size} height=${size} viewBox="0 0 48 48" aria-hidden="true">
    <defs><linearGradient id="oc-g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="var(--accent)" /><stop offset="1" stop-color="var(--accent-2)" /></linearGradient></defs>
    <circle cx="24" cy="24" r="19" fill="none" stroke="url(#oc-g)" stroke-width="3.6" stroke-dasharray="78 42" stroke-linecap="round" transform="rotate(-50 24 24)" />
    <circle cx="24" cy="24" r="10.4" fill="none" stroke="url(#oc-g)" stroke-width="3" opacity=".55" />
    <circle cx="24" cy="24" r="3.9" fill="url(#oc-g)" />
  </svg>`;
}
