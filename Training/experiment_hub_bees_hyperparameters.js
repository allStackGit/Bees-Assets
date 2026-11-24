'use strict';

// Non-architectural trainer settings only. Changing these settings requires
// an independent optimizer lineage, not an implicit checkpoint migration.
const SCHEMA=[
  [
    "batch_size",
    "PPO minibatch size",
    4096,
    1,
    65536,
    true,
    "hyperparameters.batch_size"
  ],
  [
    "buffer_size",
    "PPO buffer size",
    65536,
    64,
    4194304,
    true,
    "hyperparameters.buffer_size"
  ],
  [
    "learning_rate",
    "PPO learning rate",
    0.001,
    0.000001,
    0.1,
    false,
    "hyperparameters.learning_rate"
  ],
  [
    "beta",
    "Entropy coefficient",
    0.01,
    0,
    0.5,
    false,
    "hyperparameters.beta"
  ],
  [
    "epsilon",
    "PPO clip epsilon",
    0.15,
    0.01,
    0.5,
    false,
    "hyperparameters.epsilon"
  ],
  [
    "lambd",
    "GAE lambda",
    0.99,
    0.7,
    1,
    false,
    "hyperparameters.lambd"
  ],
  [
    "num_epoch",
    "PPO epochs",
    3,
    1,
    32,
    true,
    "hyperparameters.num_epoch"
  ],
  [
    "gamma",
    "Reward discount gamma",
    1,
    0,
    1,
    false,
    "reward_signals.extrinsic.gamma"
  ],
  [
    "time_horizon",
    "Trajectory time horizon",
    2048,
    1,
    65536,
    true,
    "time_horizon"
  ],
  [
    "self_play_save_steps",
    "Self-play snapshot interval",
    1000000,
    1,
    100000000000,
    true,
    "self_play.save_steps"
  ],
  [
    "self_play_team_change",
    "Self-play team change interval",
    500000,
    1,
    100000000000,
    true,
    "self_play.team_change"
  ],
  [
    "self_play_swap_steps",
    "Self-play opponent swap interval",
    500000,
    1,
    100000000000,
    true,
    "self_play.swap_steps"
  ],
  [
    "self_play_window",
    "Maximum self-play snapshots",
    30,
    1,
    1024,
    true,
    "self_play.window"
  ],
  [
    "self_play_latest_ratio",
    "Probability of latest self-play opponent",
    0.2,
    0,
    1,
    false,
    "self_play.play_against_latest_model_ratio"
  ],
  [
    "self_play_initial_elo",
    "Initial self-play ELO",
    1200,
    0,
    100000,
    false,
    "self_play.initial_elo"
  ],
  [
    "summary_freq",
    "TensorBoard summary interval (steps)",
    50000,
    1,
    1000000000,
    true,
    "summary_freq"
  ],
  [
    "keep_checkpoints",
    "Retained optimizer checkpoints",
    250,
    1,
    1000,
    true,
    "keep_checkpoints"
  ]
];
function inRange(value,row) {
    return typeof value==='number'&&Number.isFinite(value)&&
        value>=row[3]&&value<=row[4]&&(!row[5]||Number.isSafeInteger(value));
}
function fields(protocol=3,defaults=null) {
    return SCHEMA.slice(0,protocol>=3?SCHEMA.length:7).map(row=>{
        const [key,label,baseline,min,max,integer]=row;
        const value=defaults?defaults[key]:baseline;
        if(!inRange(value,row))throw Error('Pinned Bees trainer has an unsupported '+key+' default.');
        return {key:'hyperparameters.'+key,label,section:'training',
            type:integer?'integer':'number',default:value,min,max};
    });
}
function validate(overrides={},protocol=3,defaults=null) {
    if(!overrides||typeof overrides!=='object'||Array.isArray(overrides))
        throw Error('Bees trainer overrides must be an object.');
    const rows=SCHEMA.slice(0,protocol>=3?SCHEMA.length:7);
    const table=new Map(rows.map(row=>[row[0],row]));
    for(const [key,value] of Object.entries(overrides))
        if(!table.has(key)||!inRange(value,table.get(key)))
            throw Error('Invalid or unsupported Bees trainer setting: '+key);
    if(defaults||Object.hasOwn(overrides,'batch_size')&&Object.hasOwn(overrides,'buffer_size')) {
        const batch=overrides.batch_size??defaults.batch_size;
        const buffer=overrides.buffer_size??defaults.buffer_size;
        if(buffer<batch||buffer%batch!==0)
            throw Error('Bees PPO buffer size must be a positive multiple of minibatch size.');
    }
    return {...overrides};
}
module.exports={SCHEMA,fields,validate};
