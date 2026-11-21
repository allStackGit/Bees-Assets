'use strict';

// The optimizer's committed manifest is immutable. The next collection
// may replace exactly one frozen opponent, never the learner or scene.
const {isDeepStrictEqual}=require('node:util');
const path=require('node:path');
const fs=require('node:fs');
const SHA=/^[a-f0-9]{64}$/;
const MODEL=/^bees-rl-v[0-9]+-[0-9a-f]{24}$/;

function validateSelection(selection) {
    if(!selection||typeof selection!=='object'||Array.isArray(selection)||
        selection.schema_version!==1||selection.kind!=='native_historical_opponent_selection'||
        selection.round!==1||
        !['input_manifest_sha256','input_checkpoint_sha256','effective_manifest_sha256','plan_sha256']
            .every(key=>SHA.test(String(selection[key]||'')))||
        !Number.isSafeInteger(selection.opponent_slot)||selection.opponent_slot<0||
        selection.opponent_slot>0xffffffff||
        !MODEL.test(String(selection.champion_model_id||''))||
        (selection.opponent_model_id===null?
            selection.selected_weight!==null||selection.selected_regression!==null||
            selection.effective_manifest_sha256!==selection.input_manifest_sha256:
            !MODEL.test(String(selection.opponent_model_id||''))||
            !Number.isFinite(selection.selected_weight)||selection.selected_weight<=0||
            !Number.isFinite(selection.selected_regression)))
        throw Error('Invalid native historical opponent selection.');
    return selection;
}

function validateCollectionSelection(round,release) {
    const selection=release.collection_selection;
    if(selection===undefined) {
        if(round.hub_historical_selection===true)
            throw Error('Native generation has no committed historical selection.');
        return null;
    }
    validateSelection(selection);
    if(round.hub_historical_selection!==true||
        selection.plan_sha256!==round.continual_release_plan?.plan_sha256||
        selection.input_manifest_sha256!==round.next_manifest_sha256||
        selection.input_checkpoint_sha256!==round.next_checkpoint_sha256||
        selection.opponent_slot!==round.learner_metrics?.refreshed_opponent_slot||
        selection.opponent_slot===round.learner_metrics?.learner_slot)
        throw Error('Historical selection does not belong to the committed native generation.');
    return selection;
}

function validateCollectionManifest(base,selected,selection,local=null) {
    // Initial draws retain local dependencies; distributed handoff still
    // uses the strictly relative portable manifest contract.
    const normalize=(row,directory)=>({...row,path:fs.realpathSync(path.resolve(directory,row.path))});
    if(!Array.isArray(base?.models)||!Array.isArray(selected?.models)||
        !isDeepStrictEqual({...base,models:null},{...selected,models:null})||
        base.models.length!==selected.models.length||
        base.models.filter(row=>row.slot===selection.opponent_slot).length!==1||
        base.models.some((row,i)=>{
            const next=selected.models[i];
            if(row.slot!==selection.opponent_slot)return !isDeepStrictEqual(
                local?normalize(row,local.baseDirectory):row,
                local?normalize(next,local.selectedDirectory):next);
            return next?.slot!==row.slot||!['mlagents','onnx'].includes(next.backend)||
                !SHA.test(String(next.sha256||''))||
                (local?path.resolve(local.selectedDirectory,next.path)!==
                    path.join(local.selectedDirectory,'opponents',next.sha256+'.model'):
                    next.path!=='opponents/'+next.sha256+'.model')||
                !selection.opponent_model_id.endsWith(next.sha256.slice(0,24));
        }))
        throw Error('Historical selection changed model routes outside its frozen opponent slot.');
}

module.exports={validateSelection,validateCollectionSelection,validateCollectionManifest};
