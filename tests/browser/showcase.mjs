// Synthetic, offline-only UI showcase: no real library paths, cookies or BOOTH assets.
import { chromium } from 'playwright';
import fs from 'node:fs/promises';
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({viewport:{width:1440,height:1040},deviceScaleFactor:1});
const root='D:/AssetLibrary';
const records=[['Luna Avatar','角色',100001,'auto'],['Cloud Knit','服装',null,'review'],['Motion Toolkit','插件',100003,'auto'],['Celestial Hair','发型',null,'unknown'],['Garden World','场景',100005,'auto'],['Ribbon Collection','配饰',null,'review'],['Studio Lighting','插件',100007,'auto'],['Summer Outfit','服装',null,'unknown']];
const files=records.map(([name,category,id,status])=>({path:`${root}/${category}/${name}`,root,name,kind:'folder',category,itemId:id,title:id?name:null,status,recognitionState:id?'bound':status,members:[{path:`${root}/${category}/${name}/Package.unitypackage`,name:'Package.unitypackage',kind:'unitypackage'}],aliases:[],candidates:status==='review'?[{id:100020,name:name+' — Original',brand:'Demo Studio'}]:[],item:id?{id,title:name,shopName:'Demo Studio'}:null}));
await page.route('**/api/**',r=>{const u=new URL(r.request().url()),p=u.pathname;let data={};
 if(p==='/api/resources'){const s=u.searchParams.get('status'),q=u.searchParams.get('q');const list=files.filter(f=>(!s||f.status===s)&&(!q||f.name.includes(q)));data={files:list,total:list.length};}
 else if(p==='/api/roots')data={roots:[{path:root,mode:'library'}]};
 else if(p==='/api/scan/status')data={state:'idle'};
 else if(p==='/api/status')data={items:4,requests:0,imageRequests:0};
 else if(p==='/api/unity/projects')data={projects:[]};
 else if(p==='/api/items')data={items:[],total:0};
 else if(p==='/api/jobs')data=[];
 else if(p==='/api/structure')data={root,folders:[]};
 else if(p==='/api/session')data={token:'offline-showcase'};
 return r.fulfill({json:data});});
await page.route('**/img/**',r=>r.fulfill({status:404}));
const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.goto(process.env.BOOTHLENS_TEST_URL||'http://127.0.0.1:5181');await page.locator('.resource-card').first().waitFor();
await fs.mkdir('../../docs/screenshots',{recursive:true});
await page.screenshot({path:'../../docs/screenshots/library.png',fullPage:true});
await page.locator('.resource-open').first().click();await page.getByRole('button',{name:'清除绑定',exact:true}).waitFor();await page.screenshot({path:'../../docs/screenshots/detail.png',fullPage:true});
await page.locator('#detail-dialog [data-close]').click();
await page.setViewportSize({width:390,height:844});if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('mobile overflow');
if(errors.length)throw Error(errors.join('\n'));console.log('PASS synthetic showcase and mobile layout');await browser.close();
