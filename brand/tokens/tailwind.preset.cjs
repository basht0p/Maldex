// maldex tailwind preset · colors read the CSS variables, so themes switch with no dark: prefixes
module.exports = {
  theme: {
    extend: {
      colors: {
        'bg': 'var(--mx-bg)',
        'surface': 'var(--mx-surface)',
        'raised': 'var(--mx-raised)',
        'overlay': 'var(--mx-overlay)',
        'border': 'var(--mx-border)',
        'border-strong': 'var(--mx-border-strong)',
        'control': 'var(--mx-control)',
        'text': 'var(--mx-text)',
        'muted': 'var(--mx-muted)',
        'accent': 'var(--mx-accent)',
        'accent-hover': 'var(--mx-accent-hover)',
        'accent-pressed': 'var(--mx-accent-pressed)',
        'accent-ink': 'var(--mx-accent-ink)',
        'critical': 'var(--mx-critical)',
        'warning': 'var(--mx-warning)',
        'success': 'var(--mx-success)',
        'info': 'var(--mx-info)',
      },
      fontFamily: {
        ui: 'var(--mx-font-ui)',
        mono: 'var(--mx-font-mono)',
      },
      borderRadius: { control: '4px', panel: '8px', modal: '12px' },
    },
  },
};
