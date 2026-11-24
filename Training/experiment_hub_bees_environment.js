'use strict';

// Dedicated Unity options mirror RlOneVsOneTrainingOptions. The saved
// representation remains the native --rl-* argument list so existing job
// definitions, WAN actors and frozen Python runners share one contract.
const field=(key,label,type,def,min,max)=>({
    key:'environment_args.'+key,label,section:'environment',type,
    default:def,...(type==='number'||type==='integer'?{min,max}:{}),
});
const fields=Object.freeze([
    field('health_ratio','Starting health ratio','number',0.25,0.001,1),
    field('map_size_min','Minimum map size','number',30,10,4096),
    field('map_size_max','Maximum map size','number',30,10,4096),
    field('ships_per_side','Ships per side','integer',1,1,64),
    field('episode_timeout','Episode timeout (seconds)','integer',120,1,86400),
    field('decision_period','Physics steps per decision','integer',5,1,1000),
    {...field('matchup_mode','Matchup selection','enum','fixed'),options:['fixed','sampled']},
    field('collision_asteroid_spawn_seconds','Collision asteroid spawn interval (0 disables)','number',0,0,86400),
    field('static_obstacles','Static obstacles','boolean',false),
    field('mining_asteroids','Mining asteroids','boolean',false),
    field('disable_movement','Disable movement for both teams','boolean',false),
    field('disable_agent_movement','Disable learner movement','boolean',false),
    field('disable_opponent_movement','Disable opponent movement','boolean',false),
    field('log_communications','Communication logging','boolean',false),
    field('log_episodes','Episode logging','boolean',true),
    ...['combat','aim','aim_directions','visibility','environment','rewards','policy']
        .map(key=>field('log_'+key,'Log '+key.replaceAll('_',' '),'boolean',true)),
]);
const byFlag=new Map(fields.map(f=>['--rl-'+f.key.split('.')[1].replaceAll('_','-'),f]));
const bools={true:true,on:true,yes:true,'1':true,false:false,off:false,no:false,'0':false};
const numeric=/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i;
const rosterNames={
    '--rl-bee-ship-types':new Set(['beehive','bumblebee','carpenterbee','honeybee','hornet','leafcutter','queen','wasp','yellowjacket']),
    '--rl-human-ship-types':new Set(['barge','carrier','cruiser','dreadnought','factory','firebarge','flagship','frigate','gunship','scout','warpgate']),
};
function validateEnvironmentArgs(args=[]) {
    if(!Array.isArray(args)||args.length>32)throw Error('Bees environment accepts at most 32 options.');
    const values=new Map();
    for(const arg of args) {
        if(typeof arg!=='string'||arg.length>256||/[\r\n\x00]/.test(arg))
            throw Error('Invalid Bees Unity environment option.');
        const i=arg.indexOf('=');
        const flag=(i<0?arg:arg.slice(0,i)).toLowerCase();
        const raw=i<0?null:arg.slice(i+1);
        if(values.has(flag))throw Error('Duplicate Bees Unity option: '+flag);
        const f=byFlag.get(flag);
        let value;
        if(f?.type==='boolean') {
            const token=raw===null?'true':raw.toLowerCase();
            if(!Object.hasOwn(bools,token))throw Error('Invalid boolean for '+flag);
            value=bools[token];
        }else if(f?.type==='enum') {
            value=raw?.toLowerCase();
            if(!f.options.includes(value))throw Error('Invalid matchup mode.');
        }else if(f||flag==='--rl-map-size'||flag==='--rl-scenario-seed') {
            const min=f?.min??(flag==='--rl-map-size'?10:0);
            const max=f?.max??(flag==='--rl-map-size'?4096:2147483647);
            const integer=f?.type==='integer'||flag==='--rl-scenario-seed';
            value=Number(raw);
            if(raw===null||!numeric.test(raw)||!Number.isFinite(value)||value<min||value>max||
                integer&&!Number.isSafeInteger(value))throw Error('Invalid numeric value for '+flag);
        }else if(Object.hasOwn(rosterNames,flag)) {
            const names=raw?.split(',').map(x=>x.trim().replace(/[-_ ]/g,'').toLowerCase())||[];
            if(!names.length||names.length>64||names.some(x=>!rosterNames[flag].has(x)))
                throw Error('Invalid ship roster for '+flag);
            value=names;
        }else throw Error('Unsupported Bees training option: '+flag);
        values.set(flag,value);
    }
    const lo=values.get('--rl-map-size-min'),hi=values.get('--rl-map-size-max');
    if((lo===undefined)!==(hi===undefined)||lo!==undefined&&(
        lo>hi||values.has('--rl-map-size')))
        throw Error('Set both map range endpoints in ascending order, or one fixed map size.');
    const mode=values.get('--rl-matchup-mode')??'fixed';
    const ships=values.get('--rl-ships-per-side')??1;
    for(const key of Object.keys(rosterNames)) {
        const roster=values.get(key);
        if(roster&&(mode==='fixed'&&roster.length!==1&&roster.length!==ships||
            mode==='sampled'&&new Set(roster).size!==roster.length))
            throw Error('Fixed rosters require one repeated type or one type per ship; sampled pools must be unique.');
    }
    return args.slice();
}
module.exports={fields,validateEnvironmentArgs};
