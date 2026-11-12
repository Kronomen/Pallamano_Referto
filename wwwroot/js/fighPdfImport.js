// Estrazione mirata delle sole rose dal referto FIGH.
window.fighPdfImport = {
  read: async function(base64) {
    if (!window.pdfjsLib) throw new Error('Lettore PDF non disponibile. Verifica la connessione e ricarica la pagina.');
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const pdf = await pdfjsLib.getDocument({ data: bytes }).promise;
    if (pdf.numPages !== 1) throw new Error('È supportato il referto FIGH standard di una pagina.');
    const page = await pdf.getPage(1);
    const viewport = page.getViewport({ scale: 1 });
    const content = await page.getTextContent();
    const items = (content.items || []).filter(x => x.str && x.str.trim()).map(x => ({
      text: x.str.trim(), x: x.transform[4] / viewport.width,
      y: 1 - x.transform[5] / viewport.height
    }));
    if (!items.length) throw new Error('Questo PDF non contiene testo selezionabile. Per questo file serve una lettura delle sole due tabelle come immagine.');
    const lines=[];
    for(const item of items){
      let line=lines.find(l=>Math.abs(l.y-item.y)<0.004);
      if(!line){line={y:item.y,items:[]};lines.push(line);}
      line.items.push(item);
    }
    lines.sort((a,b)=>a.y-b.y);
    for(const l of lines) l.items.sort((a,b)=>a.x-b.x);
    const lineText=l=>l.items.map(i=>i.text).join(' ').replace(/\\s+/g,' ').trim();
    const seasonText=items.map(i=>i.text).join(' ');
    const sm=seasonText.match(/20\\d{2}\\s*[/\\-]\\s*20\\d{2}/);
    const season=sm?sm[0].replace(/\\s/g,'').replace('-', '/'):'';
    const teamAt=y=>{
      const l=lines.reduce((best,x)=>Math.abs(x.y-y)<Math.abs(best.y-y)?x:best,lines[0]);
      return l?lineText(l).replace(/^[AB]\\s*/i,'').trim():'';
    };
    const bands=[
      {teamIndex:0,min:.12,max:.53},
      {teamIndex:1,min:.55,max:.91}
    ];
    const people=[];
    for(const band of bands){
      for(const l of lines){
        if(l.y<band.min||l.y>band.max) continue;
        const left=l.items.filter(i=>i.x<.16).map(i=>i.text).join('').replace(/[^A-Z0-9.]/gi,'').toUpperCase();
        const name=l.items.filter(i=>i.x>=.16&&i.x<.42).map(i=>i.text).join(' ').replace(/\\s+/g,' ').trim();
        if(!name || name.length<3) continue;
        const official=left.match(/^UFF\\.?([A-E])$/i);
        if(official){
          people.push({teamIndex:band.teamIndex,type:'OFFICIAL',number:'',letter:official[1].toUpperCase(),name});
          continue;
        }
        if(/^\\d{1,2}$/.test(left)){
          people.push({teamIndex:band.teamIndex,type:'PLAYER',number:left,letter:'',name});
        }
      }
    }
    const normalize=s=>(s||'').normalize('NFD').replace(/[\\u0300-\\u036f]/g,'').replace(/[^a-z0-9]/gi,'').toUpperCase();
    const unique=[];
    for(const p of people){
      if(!unique.some(x=>x.teamIndex===p.teamIndex&&x.type===p.type&&normalize(x.name)===normalize(p.name))) unique.push(p);
    }
    if(unique.length<2) throw new Error('Non sono riuscito a individuare le righe delle due rose nel testo del PDF. Nessun dato è stato importato.');
    return JSON.stringify({season,teams:[teamAt(.125),teamAt(.56)],people:unique,pageCount:pdf.numPages,method:'pdf-text'});
  }
};