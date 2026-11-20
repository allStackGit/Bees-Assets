#!/usr/bin/env node
'use strict';

// Generate the one-file Windows installer from its checked-in PowerShell
// source. This is a packaging step, not a release download or service update.
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const root=__dirname;
const source=path.join(root,'experiment_hub_worker_install.ps1');
const target=path.join(root,'experiment_hub_worker_install.cmd');
const prefix="@echo off\r\nsetlocal DisableDelayedExpansion\r\necho [Experiment Hub] Preparing the Windows worker installer...\r\nwhere powershell.exe >nul 2>&1\r\nif errorlevel 1 (\r\n    echo [Experiment Hub] Windows PowerShell is required.\r\n    set \"HUB_WORKER_EXIT=1\"\r\n    goto HUB_WORKER_DONE\r\n)\r\nset \"HUB_WORKER_SELF=%~f0\"\r\nset \"HUB_WORKER_SCRIPT=%TEMP%\\experiment-hub-worker-%RANDOM%-%RANDOM%.ps1\"\r\npowershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^\r\n \"$raw=[IO.File]::ReadAllText($env:HUB_WORKER_SELF); $tag=[regex]::Match($raw,'(?m)^:__EXPERIMENT_HUB_POWERSHELL_PAYLOAD__\\r?\\n'); if(-not $tag.Success){throw 'Embedded worker payload is missing.'}; $payload=$raw.Substring($tag.Index+$tag.Length); $tokens=$null; $errors=$null; $null=[System.Management.Automation.Language.Parser]::ParseInput($payload,[ref]$tokens,[ref]$errors); if($errors.Count){foreach($item in $errors){[Console]::Error.WriteLine($item)};exit 2}; $bytes=([System.Text.UTF8Encoding]::new($false)).GetBytes($payload); $stream=[IO.File]::Open($env:HUB_WORKER_SCRIPT,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None); try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}\"\r\nif errorlevel 1 (\r\n    echo [Experiment Hub] Installer extraction or PowerShell syntax validation failed.\r\n    set \"HUB_WORKER_EXIT=1\"\r\n    goto HUB_WORKER_DONE\r\n)\r\npowershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%HUB_WORKER_SCRIPT%\" %*\r\nset \"HUB_WORKER_EXIT=%ERRORLEVEL%\"\r\ndel /f /q \"%HUB_WORKER_SCRIPT%\" >nul 2>&1\r\n:HUB_WORKER_DONE\r\nif \"%HUB_WORKER_EXIT%\"==\"223\" exit /b 0\r\nif not \"%HUB_WORKER_EXIT%\"==\"0\" echo [Experiment Hub] Installer failed, exit code %HUB_WORKER_EXIT%.\r\nif \"%HUB_WORKER_EXIT%\"==\"0\" echo [Experiment Hub] Installer finished successfully.\r\necho [Experiment Hub] Review the result above. Press any key to close this window.\r\npause >nul\r\nexit /b %HUB_WORKER_EXIT%\r\n:__EXPERIMENT_HUB_POWERSHELL_PAYLOAD__\r\n";
const mode=process.argv[2]||'--check';
if(!['--check','--write'].includes(mode)||process.argv.length>3)
    throw Error('Use: node Training/generate_experiment_hub_worker_cmd.js --write|--check');
const ps=fs.readFileSync(source,'utf8');
if(ps.charCodeAt(0)===0xfeff||!ps.startsWith('# One machine-local Hub agent')||
    !ps.includes('[CmdletBinding()]')||!ps.includes('param(')||
    !ps.endsWith('\n'))
    throw Error('Unexpected PowerShell worker installer source format.');
const output=prefix+ps.replace(/\r?\n/g,'\r\n');
const digest=crypto.createHash('sha256').update(output).digest('hex');
if(mode==='--check') {
    if(!fs.existsSync(target)||fs.readFileSync(target,'utf8')!==output) {
        console.error('Generated Windows worker CMD is out of date. Run --write and commit both files.');
        process.exitCode=1;
    }else console.log('Windows CMD matches current PowerShell source: '+digest);
}else {
    const temp=target+'.'+process.pid+'.tmp';
    try {
        fs.writeFileSync(temp,output,{encoding:'utf8',flag:'wx'});
        fs.renameSync(temp,target);
    }finally {
        if(fs.existsSync(temp))fs.unlinkSync(temp);
    }
    console.log('Generated '+path.basename(target)+' SHA-256 '+digest);
}
