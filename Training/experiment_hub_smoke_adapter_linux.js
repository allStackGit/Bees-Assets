'use strict';

// Inert Linux-only local adapter for an operator-approved native Hub smoke.
// Never reads configuration-supplied commands, paths, scripts or arguments.
// Do not use as a production training adapter.
module.exports={
    smoke:{
        executable:'/usr/bin/sleep',
        stop_policy:'terminate-group',
        args:parameters=>{
            if(!parameters||parameters.kind!=='inert'||
                Object.keys(parameters).length!==1)
                throw Error('Smoke job must freeze exactly {kind:"inert"}.');
            return ['30'];
        },
    },
};
