$ErrorActionPreference = 'Stop'
$assetByGuid = @{}
rg '^guid: ' Assets Library/PackageCache -g '*.meta' | ForEach-Object {
    if ($_.Trim() -match '^(.*)\.meta:guid: ([0-9a-f]{32})$') { $assetByGuid[$Matches[2]] = $Matches[1] }
}
$prefabs = @{}
Get-ChildItem Assets/TerrainSystem/Layers -Filter *.asset | ForEach-Object {
    $layer = $_.Name
    [regex]::Matches((Get-Content -LiteralPath $_.FullName -Raw),'prefab: \{[^}]*guid: ([0-9a-f]{32})') | ForEach-Object {
        $id=$_.Groups[1].Value
        if(-not $prefabs.ContainsKey($id)) { $prefabs[$id]=[System.Collections.Generic.List[string]]::new() }
        $prefabs[$id].Add($layer)
    }
}
$rows = foreach($id in $prefabs.Keys) {
    $queue=[System.Collections.Generic.Queue[string]]::new();$queue.Enqueue($id);$visited=@{};$mats=@{}
    while($queue.Count -gt 0) {
        $next=$queue.Dequeue();if($visited.ContainsKey($next)){continue};$visited[$next]=$true
        $path=$assetByGuid[$next];if(-not $path){continue}
        if($path.EndsWith('.mat')){$mats[$path]=$true;continue}
        if($path.EndsWith('.fbx',[StringComparison]::OrdinalIgnoreCase)){$path+='.meta'}
        elseif(-not $path.EndsWith('.prefab')){continue}
        [regex]::Matches((Get-Content -LiteralPath $path -Raw),'guid: ([0-9a-f]{32})') | ForEach-Object {$queue.Enqueue($_.Groups[1].Value)}
    }
    foreach($mat in $mats.Keys) {
        $data=Get-Content -LiteralPath $mat -Raw
        $shader=[regex]::Match($data,'m_Shader: \{[^}]*guid: ([0-9a-f]{32})').Groups[1].Value
        [pscustomobject]@{Prefab=$assetByGuid[$id];Material=$mat;Shader=$assetByGuid[$shader];ShaderGuid=$shader;Layers=($prefabs[$id] -join ',')}
    }
}
$rows | Sort-Object Shader,Prefab,Material | Format-Table Prefab,Material,ShaderGuid -AutoSize | Out-String -Width 360
$rows | Group-Object Shader | ForEach-Object {[pscustomobject]@{Shader=$_.Name;PrefabMaterialPairs=$_.Count;Prefabs=($_.Group.Prefab|Sort-Object -Unique).Count;Materials=($_.Group.Material|Sort-Object -Unique).Count}} | Format-List
"Layer prefab references: $($prefabs.Count) unique. Static asset dependency inventory, not runtime timing/counts."
