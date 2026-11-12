// Importazione referti FIGH standard (PDF rasterizzati tramite OCR).
window.fighPdfImport = {
  read: async function(base64) {
    if (!window.pdfjsLib || !window.Tesseract) throw new Error('Lettori PDF/OCR non disponibili. Verifica la connessione e ricarica la pagina.');
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const pdf = await pdfjsLib.getDocument({ data: bytes }).promise;
    if (pdf.numPages !== 1) throw new Error('Per ora è supportato il referto FIGH standard di una pagina.');
    const page = await pdf.getPage(1);
    const viewport = page.getViewport({ scale: 2.5 });
    const canvas = document.createElement('canvas');
    canvas.width = Math.ceil(viewport.width); canvas.height = Math.ceil(viewport.height);
    await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
    const worker = await Tesseract.createWorker('ita');
    try {
      const result = await worker.recognize(canvas);
      const words = (result.data.words || []).filter(w => (w.text || '').trim());
      const W = canvas.width, H = canvas.height;
      const lines = [];
      for (const word of words) {
        const y = (word.bbox.y0 + word.bbox.y1) / 2;
        let line = lines.find(x => Math.abs(x.y - y) < H * 0.009);
        if (!line) { line = { y, words: [] }; lines.push(line); }
        line.words.push(word);
      }
      lines.sort((a,b) => a.y-b.y);
      for (const line of lines) line.words.sort((a,b) => a.bbox.x0-b.bbox.x0);
      const norm = s => (s || '').normalize('NFD').replace(/[\\u0300-\\u036f]/g,'').replace(/[^a-z0-9]/gi,'').toUpperCase();
      const textLines = lines.map(l => l.words.map(w => w.text).join(' '));
      const header = textLines.join(' ');
      const seasonMatch = header.match(/20\\d{2}\\s*[/\\-]\\s*20\\d{2}/);
      const season = seasonMatch ? seasonMatch[0].replace(/\\s/g,'').replace('-', '/') : '';
      const teamRows = [];
      const addBand = (teamIndex, yMin, yMax, mode) => {
        for (const line of lines) {
          const yn = line.y / H;
          if (yn < yMin || yn > yMax) continue;
          const left = line.words.filter(w => w.bbox.x0 / W < 0.15);
          const nameWords = line.words.filter(w => w.bbox.x0 / W >= 0.15 && w.bbox.x0 / W < 0.49);
          if (!left.length || !nameWords.length) continue;
          const first = left.map(w => w.text).join('').replace(/[^A-Z0-9.]/gi,'').toUpperCase();
          const name = nameWords.map(w => w.text).join(' ').replace(/\\s+/g,' ').trim();
          if (/^UFF\\.?[A-E]$/.test(first) || /^UFF[A-E]$/.test(first)) {
            const letter = first.replace(/[^A-E]/g,'').slice(-1);
            if (letter && name.length > 2) teamRows.push({ teamIndex, type:'OFFICIAL', number:'', letter, name, y:yn });
          } else if (mode === 'PLAYER' && name.length > 3 && !/^UFF/i.test(first)) {
            const number = /^\\d{1,2}$/.test(first) ? first : '';
            teamRows.push({ teamIndex, type:'PLAYER', number, letter:'', name, y:yn });
          }
        }
      };
      // Le coordinate seguono la gabbia del referto FIGH A4: due rose verticali.
      addBand(0, 0.145, 0.465, 'PLAYER');
      addBand(0, 0.465, 0.515, 'OFFICIAL');
      addBand(1, 0.55, 0.80, 'PLAYER');
      addBand(1, 0.80, 0.875, 'OFFICIAL');
      const teamNameAt = (yTarget) => {
        const line = lines.reduce((best,l) => {
          const yn=l.y/H; return Math.abs(yn-yTarget)<Math.abs(best.y/H-yTarget)?l:best;
        }, lines[0]);
        return line ? line.words.filter(w=>w.bbox.x0/W>0.15 && w.bbox.x0/W<0.52).map(w=>w.text).join(' ').trim() : '';
      };
      const teams = [teamNameAt(0.124), teamNameAt(0.55)];
      const cleanTeam = s => s.replace(/^(A|B)\\s+/i,'').replace(/\\s+/g,' ').trim();
      const unique = [];
      for (const row of teamRows.sort((a,b)=>a.teamIndex-b.teamIndex || a.y-b.y)) {
        if (!unique.some(x=>x.teamIndex===row.teamIndex && x.type===row.type && norm(x.name)===norm(row.name))) unique.push(row);
      }
      if (unique.length < 2) throw new Error('Non sono riuscito a leggere le rose. Verifica che il PDF sia un referto FIGH standard leggibile.');
      return JSON.stringify({ season, teams: teams.map(cleanTeam), people: unique.map(({y,...x})=>x), pageCount: pdf.numPages });
    } finally { await worker.terminate(); }
  }
};
