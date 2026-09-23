// js/solar-icon.js
fetch('assets/solar.json')
  .then(response => response.json())
  .then(data => {
    if (window.Iconify && typeof Iconify.addCollection === 'function') {
      Iconify.addCollection(data);
      console.log('✅ Solar icon collection loaded from local file');
    } else {
      console.warn('⚠ Iconify not ready, cannot add solar collection');
    }
  })
  .catch(err => {
    console.error('❌ Failed to load solar icon collection:', err);
  });
