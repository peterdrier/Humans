export const SOUND_ZONE_FILL_EXPR = ['match', ['get', 'soundZone'],
    0, '#88aadd', 1, '#88bb88', 2, '#ddcc66', 3, '#ddaa66', 4, '#dd8888', '#aaaaaa'];

export const SOUND_ZONE_LINE_EXPR = ['match', ['get', 'soundZone'],
    0, '#2266cc', 1, '#229944', 2, '#cc9900', 3, '#cc6600', 4, '#cc1111', 5, '#cc00cc', '#666666'];

// 5 ("surprise") matches any zone, so it never produces a mismatch.
export const SOUND_ZONE_NAMES = { 0: 'blue', 1: 'green', 2: 'yellow', 3: 'orange', 4: 'red' };
