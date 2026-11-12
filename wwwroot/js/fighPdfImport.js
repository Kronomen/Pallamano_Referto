// Importazione mirata delle sole due tabelle rose del referto FIGH.
window.fighPdfImport = {
  read: async function(base64) {
    if (!window.pdfjsLib || !window.Tesseract) throw new Error('Lettori PDF/OCR non disponibili. Verifica la connessione e ricarica la pagina.');
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const pdf = await pdfjsLib.getDocument({ data: bytes }).promise;
    if (pdf.numPages !== 1) throw new Error('È supportato il referto FIGH standard di una pagina.');
    const page = await pdf.getPage(1);
    const viewport = page.getViewport({ scale: 4 });
    const source = document.createElement('canvas');
    source.width = Math.ceil(viewport.width); source.height = Math.ceil(viewport.height);
    await page.render({ canvasContext: source.getContext('2d'), viewport }).promise;

    // Coordinate normalizzate della gabbia delle due rose sul modello FIGH A4.
    const regions = [
      { teamIndex: 0, x: .085, y: .102, w: .755, h: .385 },
      { teamIndex: 1, x: .085, y: .505, w: .755, h: .385 }
    ];
    const worker = await Tesseract.createWorker('ita');
    try {
      await worker.setParameters({ tessedit_pageseg_mode: '6', preserve_interword_spaces: '1' });
      const people = [], teams = ['', ''];
      for (const region of regions) {
        const crop = document.createElement('canvas');
        crop.width = Math.round(source.width * region.w);
        crop.height = Math.round(source.height * region.h);
        crop.getContext('2d').drawImage(source,
          Math.round(source.width * region.x), Math.round(source.height * region.y),
          crop.width, crop.height, 0, 0, crop.width, crop.height);
        const result = await worker.recognize(crop);
        const words = (result.data.words || []).filter(w => (w.text || '').trim());
        const lines = [];
        for (const word of words) {
          const cy = (word.bbox.y0 + word.bbox.y1) / 2;
          let line = lines.find(x => Math.abs(x.y - cy) < crop.height * .012);
          if (!line) { line = { y: cy, words: [] }; lines.push(line); }
          line.words.push(word);
        }
        lines.sort((a,b) => a.y-b.y);
        for (const line of lines) line.words.sort((a,b) => a.bbox.x0-b.bbox.x0);
        const clean = s => (s || '').replace(/\s+/g, ' ').trim();
        const normalized = s => s.normalize('NFD').replace(/[\u0300-\u036f]/g,'').toUpperCase().replace(/[^A-Z0-9]/g,'');
        // Il nome squadra è nella riga superiore della tabella, dopo la lettera A/B.
        const header = lines.find(l => l.y / crop.height < .12);
        if (header) {
          const hw = header.words.filter(w => w.bbox.x0 / crop.width > .065);
          teams[region.teamIndex] = clean(hw.map(w => w.text).join(' ')).replace(/^(AC|A|B)\s+/i, m => m.toUpperCase()==='A '||m.toUpperCase()==='B ' ? '' : m);
        }
        for (const line of lines) {
          const yn = line.y / crop.height;
          if (yn < .10 || yn > .98) continue;
          const left = line.words.filter(w => w.bbox.x0 / crop.width < .09);
          const nameWords = line.words.filter(w => w.bbox.x0 / crop.width >= .09 && w.bbox.x0 / crop.width < .57);
          if (!left.length || !nameWords.length) continue;
          const first = clean(left.map(w => w.text).join('')).replace(/\s/g,'').toUpperCase();
          const name = clean(nameWords.map(w => w.text).join(' '));
          if (name.length < 3) continue;
          const official = first.match(/UFF\.?([A-E])/i) || clean(line.words.map(w=>w.text).join(' ')).match(/UFF\.?\s*([A-E])/i);
          if (official) {
            people.push({ teamIndex: region.teamIndex, type:'OFFICIAL', number:'', letter:official[1].toUpperCase(), name });
          } else if (/^\d{1,2}$/.test(first)) {
            people.push({ teamIndex: region.teamIndex, type:'PLAYER', number:first, letter:'', name });
          }
        }
      }
      const key = p => p.teamIndex + '|' + p.type + '|' + (p.type === 'PLAYER' ? p.number : p.letter) + '|' + normalized(p.name);
      const unique = [];
      for (const p of people) if (!unique.some(x => key(x) === key(p))) unique.push(p);
      if (unique.filter(p=>p.type==='PLAYER').length < 2 || unique.filter(p=>p.type==='OFFICIAL').length < 1)
        throw new Error('Non sono riuscito a riconoscere correttamente le due tabelle. Controlla che il PDF sia il referto FIGH standard.');
      const seasonText = (resultText => resultText)( '');
      return JSON.stringify({ season: '2026/2027', teams, people: unique, pageCount: pdf.numPages });
    } finally { await worker.terminate(); }
  }
};
