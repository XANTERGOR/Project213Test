// Independent CPU oracle + focused source contracts for the pre-height pair.
// Run: node Tests/FastLayerPairChecks.mjs (also works from another directory).
// Does not execute HLSL, compile shaders, launch Unity, or write any files.
import assert from 'node:assert/strict';
import fs from 'node:fs';

const shaderRoot = new URL('../Assets/TerrainSystem/LocalTerrain/Shaders/', import.meta.url);
const read = name => fs.readFileSync(new URL(name, shaderRoot), 'utf8').replace(/\r\n/g, '\n');
let checks = 0, cases = 0;
function check(value, message) { checks++; assert.ok(value, message); }
function near(actual, expected, message, tolerance = 1e-10) {
    check(Number.isFinite(actual) && Number.isFinite(expected) &&
        Math.abs(actual - expected) <= tolerance * Math.max(1, Math.abs(expected)),
    `${message}: actual=${actual}, expected=${expected}`);
}
function arithmetic(q) {
    const add = (a, b) => q(q(a) + q(b)), sub = (a, b) => q(q(a) - q(b));
    const mul = (a, b) => q(q(a) * q(b)), div = (a, b) => q(q(a) / q(b));
    return {q, add, sub, mul, div, sat: x => q(Math.min(1, Math.max(0, q(x))))};
}
const precisions = [arithmetic(x => x), arithmetic(Math.fround)];

// Shared primitives only; ranking, pruning, mask defaults, height blending and
// accumulation below are implemented independently in the two algorithms.
function decodeNormal(packed, strength, flag, m) {
    const xy = packed.slice(0, 2).map(v => m.sub(m.mul(v, 2), 1));
    const z = flag < 1.5 ? m.sub(m.mul(packed[2], 2), 1) :
        m.q(Math.sqrt(Math.max(m.q(1e-16), m.sub(1, m.sat(m.add(m.mul(xy[0], xy[0]), m.mul(xy[1], xy[1])))))));
    return [...xy.map(v => m.mul(v, strength)), z];
}
function finish(color, n, ao, smoothness, metallic, axis, m) {
    n = [n[0], n[1], m.add(n[2], .00001)];
    const length = m.q(Math.sqrt(n.reduce((sum, v) => m.add(sum, m.mul(v, v)), 0)));
    const normal = n.map(v => m.div(v, length));
    const slope = normal.slice(0, 2).map(v => m.div(-v, Math.max(normal[2], m.q(.0001))));
    const gradient = axis === 0 ? [0, slope[1], slope[0]] :
        axis === 1 ? [slope[0], 0, slope[1]] : [slope[0], slope[1], 0];
    return {color, normal, gradient, ao, smoothness, metallic};
}

// Frozen policy of the former pre-height branch: independently SORT all positive
// coverage by global slice, prune a 12-slot vector, then run all 12 slots through
// the old height/default/normal math. Never normalize the pair before height.
function priorPrune12(layers, blend, axis, m) {
    const selected = layers.map((layer, slot) => ({slot, slice: layer.slice, raw: m.q(layer.raw)}))
        .filter(x => x.raw > 0).sort((a, b) => b.raw - a.raw || a.slice - b.slice || a.slot - b.slot)
        .slice(0, 2).map(x => x.slot).sort((a, b) => a - b);
    const raw = layers.map((layer, slot) => selected.includes(slot) ? m.q(layer.raw) : 0);
    const masks = layers.map((layer, slot) => {
        const mask = raw[slot] > m.q(.00001) && layer.hasMask ? layer.samples[axis].mask.map(m.q) :
            [1, .5, m.q(layer.defaultSmoothness)];
        mask[1] = m.sat(m.add(m.add(m.mul(m.sub(mask[1], .5), layer.heightGain), .5), layer.heightBias));
        return mask;
    });
    let highest = -1;
    for (let slot = 0; slot < 12; slot++)
        if (raw[slot] > m.q(.00001)) highest = Math.max(highest, m.add(masks[slot][1], raw[slot]));
    const weights = raw.map((w, slot) => {
        if (m.q(blend) <= m.q(.0001)) return w;
        const gate = m.sat(m.div(m.add(m.sub(m.add(masks[slot][1], w), highest), .2), .2));
        return m.mul(w, m.add(1, m.mul(blend, m.sub(gate, 1))));
    });
    const denominator = Math.max(weights.reduce(m.add, 0), m.q(.00001));
    for (let slot = 0; slot < 12; slot++) weights[slot] = m.div(weights[slot], denominator);
    const color = [0, 0, 0], normal = [0, 0, 0];
    let ao = 0, smoothness = 0, metallic = 0;
    for (let slot = 0; slot < 12; slot++) {
        const layer = layers[slot], weight = weights[slot];
        if (weight <= 0) continue;
        let c = [0, 0, 0], n = slot < 8 ? [0, 0, 1] : [0, 0, 0];
        if (raw[slot] > m.q(.00001)) {
            c = layer.samples[axis].color.map((v, i) => m.mul(v, layer.tint[i]));
            n = layer.normalFlag > .5 ? decodeNormal(layer.samples[axis].normal, layer.normalStrength, layer.normalFlag, m) : [0, 0, 1];
        }
        for (let i = 0; i < 3; i++) {
            color[i] = m.add(color[i], m.mul(c[i], weight));
            normal[i] = m.add(normal[i], m.mul(n[i], weight));
        }
        ao = m.add(ao, m.mul(m.sat(m.add(1, m.mul(m.sub(masks[slot][0], 1), layer.aoStrength))), weight));
        smoothness = m.add(smoothness, m.mul(masks[slot][2], weight));
        metallic = m.add(metallic, m.mul(layer.metallic, weight));
    }
    return {selected, weights, ...finish(color, normal, ao, smoothness, metallic, axis, m)};
}

// Two-record model: streaming rank, slot-ordered records, two samples. This does
// not call the oracle or build any 12-slot intermediate material/weight arrays.
function preparePair(layers, m) {
    let first = {slot: 0, raw: 0, slice: 1e20}, second = {...first};
    const ahead = (a, b) => a.raw > b.raw || (a.raw === b.raw && a.slice < b.slice);
    for (let slot = 0; slot < layers.length; slot++) {
        const candidate = {slot, raw: m.q(layers[slot].raw), slice: layers[slot].slice};
        if (candidate.raw <= 0) continue;
        if (ahead(candidate, first)) { second = first; first = candidate; }
        else if (ahead(candidate, second)) second = candidate;
    }
    if (first.slot === second.slot) second = {...second, raw: 0};
    if (first.slot > second.slot) [first, second] = [second, first];
    return [first, second].map(record => ({...layers[record.slot], ...record}));
}
function samplePair(pair, blend, axis, m) {
    const samples = pair.map(layer => {
        let ao = 1, height = .5, smoothness = m.q(layer.defaultSmoothness);
        if (layer.raw > m.q(.00001) && layer.hasMask)
            [ao, height, smoothness] = layer.samples[axis].mask.map(m.q);
        height = m.sat(m.add(m.add(m.mul(m.sub(height, .5), layer.heightGain), .5), layer.heightBias));
        return {ao, height, smoothness};
    });
    let highest = -1;
    for (let i = 0; i < 2; i++) if (pair[i].raw > m.q(.00001))
        highest = Math.max(highest, m.add(samples[i].height, pair[i].raw));
    const weights = pair.map((layer, i) => {
        let weight = layer.raw;
        if (m.q(blend) > m.q(.0001)) {
            const gate = m.sat(m.div(m.add(m.sub(m.add(samples[i].height, weight), highest), .2), .2));
            weight = m.mul(weight, m.add(1, m.mul(blend, m.sub(gate, 1))));
        }
        return weight;
    });
    const denominator = Math.max(m.add(weights[0], weights[1]), m.q(.00001));
    weights[0] = m.div(weights[0], denominator); weights[1] = m.div(weights[1], denominator);
    let ao = 0, smoothness = 0, metallic = 0;
    const color = [0, 0, 0], normal = [0, 0, 0];
    for (let i = 0; i < 2; i++) {
        const layer = pair[i], sample = samples[i], weight = weights[i];
        if (weight <= 0) continue;
        let c = [0, 0, 0], n = [0, 0, layer.slot < 8 ? 1 : 0];
        if (layer.raw > m.q(.00001)) {
            c = layer.samples[axis].color.map((v, component) => m.mul(layer.tint[component], v));
            n = [0, 0, 1];
            if (layer.normalFlag > .5) n = decodeNormal(layer.samples[axis].normal, layer.normalStrength, layer.normalFlag, m);
        }
        for (let component = 0; component < 3; component++) {
            color[component] = m.add(color[component], m.mul(c[component], weight));
            normal[component] = m.add(normal[component], m.mul(n[component], weight));
        }
        ao = m.add(ao, m.mul(m.sat(m.add(1, m.mul(m.sub(sample.ao, 1), layer.aoStrength))), weight));
        smoothness = m.add(smoothness, m.mul(sample.smoothness, weight));
        metallic = m.add(metallic, m.mul(layer.metallic, weight));
    }
    return {weights, ...finish(color, normal, ao, smoothness, metallic, axis, m)};
}
function compare(layers, blend, label) {
    for (const [precision, m] of precisions.entries()) {
        const pair = preparePair(layers, m); // Once, reused for all three axes.
        const selected = pair.filter(x => x.raw > 0).map(x => x.slot);
        check(pair[0].slot <= pair[1].slot, `${label}: ascending accumulation`);
        for (let axis = 0; axis < 3; axis++) {
            const expected = priorPrune12(layers, blend, axis, m), actual = samplePair(pair, blend, axis, m);
            checks++; assert.deepEqual(selected, expected.selected, `${label}: selected slots`);
            const context = `${label}, precision=${precision}, axis=${axis}`;
            for (let slot = 0; slot < 12; slot++) {
                const w = pair.reduce((sum, record, i) => record.slot === slot ? sum + actual.weights[i] : sum, 0);
                near(w, expected.weights[slot], `${context}: weight ${slot}`);
            }
            for (const key of ['color', 'normal', 'gradient'])
                for (let i = 0; i < 3; i++) near(actual[key][i], expected[key][i], `${context}: ${key}[${i}]`);
            for (const key of ['ao', 'smoothness', 'metallic']) near(actual[key], expected[key], `${context}: ${key}`);
            cases++;
        }
    }
}
function palette() {
    return Array.from({length: 12}, (_, slot) => ({
        slice: slot, raw: 0, tint: [1, .8, 1.2], hasMask: true, normalFlag: 1,
        normalStrength: .8, aoStrength: 1.7, heightGain: 1, heightBias: 0,
        defaultSmoothness: .37, metallic: slot / 12,
        samples: Array.from({length: 3}, (_, axis) => ({
            color: [.1 + slot * .04, .2 + axis * .1, .3],
            mask: [.3 + axis * .2, .5, .2 + slot * .03], normal: [.6, .4, .95],
        })),
    }));
}
function setHeights(layers, values) {
    values.forEach((height, slot) => layers[slot].samples.forEach(sample => { sample.mask[1] = height; }));
}

const blends = [0, .000099, .0001, .000101, .5, 1];
for (const blend of blends) {
    compare(palette(), blend, 'empty');
    for (let slot = 0; slot < 12; slot++) for (const raw of [1e-12, .000009, .00001, .000011, 1]) {
        const layers = palette(); layers[slot].raw = raw;
        compare(layers, blend, `single slot ${slot}, raw ${raw}`);
        layers[(slot + 7) % 12].raw = .75;
        compare(layers, blend, `retained second slot ${slot}, raw ${raw}`);
    }
}
for (const hasMask of [false, true]) for (const normalFlag of [0, 1, 2])
for (const heightGain of [0, .3, 1, 4]) for (const heightBias of [-1, -.2, 0, .3, 1]) {
    const layers = palette(); layers[11].raw = .32; layers[8].raw = .28; layers[0].raw = .2;
    for (const layer of layers) Object.assign(layer, {hasMask, normalFlag, heightGain, heightBias});
    setHeights(layers, [1, .5, .5, .5, .5, .5, .5, .5, .7, .5, .5, .2]);
    for (const blend of [0, .5, 1]) compare(layers, blend, `defaults/controls ${hasMask}/${normalFlag}/${heightGain}/${heightBias}`);
}

// Directed answers prevent two equivalent-looking implementations from agreeing
// on the wrong policy (early normalization, height-first selection, tiny defaults).
{
    const m = precisions[0], layers = palette();
    [layers[0].raw, layers[1].raw, layers[2].raw, layers[3].raw] = [.35, .30, .20, .15];
    setHeights(layers, [.15, .3, 1, 1]);
    const pair = preparePair(layers, m), result = samplePair(pair, 1, 1, m);
    near(result.weights[0], .175 / .475, 'authored magnitude preserved until height');
    near(result.weights[1], .30 / .475, 'discarded tall layer cannot win');
    compare(layers, 1, 'early-normalization/height-winner trap');
    const normalizedTooEarly = pair.map(x => ({...x, raw: x.raw / .65}));
    check(Math.abs(samplePair(normalizedTooEarly, 1, 1, m).weights[0] - result.weights[0]) > .05,
        'fixture must detect early normalization');
    layers[0].samples[0].mask[1] = 1; layers[1].samples[0].mask[1] = 0;
    layers[0].samples[2].mask[1] = 0; layers[1].samples[2].mask[1] = 1;
    compare(layers, 1, 'shared pair, different projected heights');
    const projectedPair = preparePair(layers, m);
    near(samplePair(projectedPair, 1, 0, m).weights[0], 1, 'first projection height winner');
    near(samplePair(projectedPair, 1, 2, m).weights[1], 1, 'third projection height winner');
}
{
    const m = precisions[0], layers = palette(); layers[11].raw = .000002;
    const tiny = samplePair(preparePair(layers, m), 0, 1, m);
    near(tiny.ao, .2, 'tiny total uses epsilon denominator, not unit normalization');
    near(tiny.smoothness, .2 * layers[11].defaultSmoothness, 'tiny slot uses default mask');
    check(tiny.color.every(x => x === 0), 'tiny slot must not sample color');
    for (const slot of [7, 8, 9, 10, 11]) {
        const p = palette(); p[0].raw = .000011; p[slot].raw = .000009;
        const n = samplePair(preparePair(p, m), 0, 1, m).normal;
        const decoded = decodeNormal(p[0].samples[1].normal, p[0].normalStrength, 1, m);
        const z = decoded[2] * .55 + (slot < 8 ? .45 : 0) + .00001;
        near(n[0] / n[2], decoded[0] * .55 / z, `tiny slot ${slot} normal default`);
    }
    const missing = palette(); missing[9].raw = 1; missing[9].hasMask = false; missing[9].normalFlag = 0;
    const plain = samplePair(preparePair(missing, m), 1, 1, m);
    near(plain.ao, 1, 'missing mask neutral AO');
    near(plain.smoothness, .37, 'missing mask default smoothness');
    check(plain.normal.every((v, i) => v === (i === 2 ? 1 : 0)), 'missing normal is flat');
    near(decodeNormal([.75, .5, .1], 2, 2, m)[2], Math.sqrt(.75), 'BC5 reconstructs Z before normal strength');
    near(decodeNormal([.75, .5, .1], 2, 1, m)[2], -.8, 'RGB normal keeps encoded Z');
}

let seed = 0x213f17;
function random() { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 2 ** 32; }
function shuffled(values) {
    const result = [...values];
    for (let i = result.length - 1; i > 0; i--) { const j = Math.floor(random() * (i + 1)); [result[i], result[j]] = [result[j], result[i]]; }
    return result;
}
for (let trial = 0; trial < 100; trial++) {
    const layers = shuffled(palette()); layers.forEach(layer => { layer.raw = .2; });
    const pair = preparePair(layers, precisions[0]);
    checks++; assert.deepEqual(pair.map(x => x.slice).sort((a, b) => a - b), [0, 1], 'ties follow global slices across permutations');
    compare(layers, .5, `equal-weight permutation ${trial}`);
}
{
    const layers = palette(); layers.forEach(x => { x.raw = .2; x.slice = 5; });
    checks++; assert.deepEqual(preparePair(layers, precisions[0]).map(x => x.slot), [0, 1], 'duplicate slice ties retain slot order');
    compare(layers, 1, 'duplicate palette slices');
}
for (let trial = 0; trial < 3000; trial++) {
    const layers = shuffled(palette());
    for (const layer of layers) {
        const kind = Math.floor(random() * 6);
        layer.raw = kind === 0 ? 0 : kind === 1 ? random() * .00002 : kind === 2 ? .2 : random();
        layer.hasMask = random() > .3; layer.normalFlag = Math.floor(random() * 3);
        layer.normalStrength = random() * 2; layer.aoStrength = random() * 3;
        layer.heightGain = random() * 4; layer.heightBias = random() * 2 - 1;
        layer.defaultSmoothness = random(); layer.metallic = random(); layer.tint = [random() * 2, random(), random()];
        for (const sample of layer.samples) {
            sample.mask = [random(), random(), random()]; sample.color = [random(), random(), random()];
            sample.normal = [.1 + random() * .8, .1 + random() * .8, .6 + random() * .4];
        }
    }
    // Exercise normalized authored coverage as well as sub-epsilon/unnormalized inputs.
    if (trial % 2) { const total = layers.reduce((sum, x) => sum + x.raw, 0); if (total) layers.forEach(x => { x.raw /= total; }); }
    compare(layers, trial % 3 === 0 ? 1 : trial % 3 === 1 ? 0 : random(), `seeded trial ${trial}`);
}

// Read only the four relevant shader sources. This small directive selector is
// NOT an HLSL compiler; fail explicitly if their conditional syntax evolves.
const stripComments = s => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '');
const sources = new Map(['LTLayerBlendCore.hlsl', 'LTFastLayerBlend.hlsl', 'LTFastLayerPair.hlsl', 'LTProjectedLayers.hlsl']
    .map(name => [name, stripComments(read(name))]));
function selectSource(name, defines) {
    const stack = [], output = []; let active = true;
    for (const line of sources.get(name).split('\n')) {
        const directive = line.match(/^\s*#(\w+)\s*(.*)$/);
        if (!directive) { if (active) output.push(line); continue; }
        const [, command, arg] = directive;
        if (command === 'if') {
            const condition = arg.trim().match(/^(!?)\s*defined\s*\(\s*(\w+)\s*\)$/);
            assert.ok(condition, `Unsupported condition in ${name}: ${arg}`);
            const match = condition[1] ? !defines.has(condition[2]) : defines.has(condition[2]);
            stack.push({parent: active, match, seenElse: false}); active = active && match;
        } else if (command === 'else') {
            const top = stack.at(-1); assert.ok(top && !top.seenElse, `Invalid else in ${name}`);
            top.seenElse = true; active = top.parent && !top.match;
        } else if (command === 'endif') {
            assert.ok(stack.length, `Unbalanced endif in ${name}`); active = stack.pop().parent;
        } else if (active && command === 'define') {
            defines.add(arg.trim().split(/\s+/)[0]);
        } else if (active && command === 'include') {
            const include = arg.match(/^"[^"\n]*\/([^/"\n]+)"$/)?.[1];
            output.push(sources.has(include) ? selectSource(include, defines) : line);
        } else if (active) throw new Error(`Unsupported directive in ${name}: ${command}`);
    }
    assert.equal(stack.length, 0, `Unclosed condition in ${name}`);
    return output.join('\n');
}
function functionBody(source, name) {
    const match = new RegExp(`\\bvoid\\s+${name}\\s*\\(`).exec(source);
    assert.ok(match, `Missing function ${name}`);
    const start = source.indexOf('{', match.index); assert.ok(start >= 0);
    let depth = 1, end = start + 1;
    for (; end < source.length && depth; end++) { if (source[end] === '{') depth++; if (source[end] === '}') depth--; }
    assert.equal(depth, 0, `Unclosed function ${name}`);
    return source.slice(start + 1, end - 1);
}
const arrayDeclaration = /\b(?:float[1-4]?|int[1-4]?|uint[1-4]?|LTLayerPair\w+)\s+\w+\s*\[/;
check(!arrayDeclaration.test(sources.get('LTFastLayerPair.hlsl')), 'pair helpers must not declare scratch arrays');
const prepare = functionBody(sources.get('LTFastLayerPair.hlsl'), 'LTPrepareLayerPair');
check((prepare.match(/\bLTChooseLayerPair\s*\(/g) || []).length === 1, 'prepare ranks once');
check((prepare.match(/\bLTLoadPairLayer\s*\(/g) || []).length === 2, 'prepare loads only two records');
const sample = functionBody(sources.get('LTFastLayerPair.hlsl'), 'LTSampleLayerPair');
check(!/\bLT(?:Choose|Prepare)LayerPair\s*\(/.test(sample), 'per-axis sampler must not rerank');
check((sample.match(/\bLTReadPairMask\s*\(/g) || []).length === 2, 'per-axis sampler reads two masks');
check((sample.match(/\bLTAccumulatePairLayer\s*\(/g) || []).length === 2, 'per-axis sampler accumulates two records');
for (const roads of [false, true]) for (const ray of [false, true]) {
    const extras = [...(roads ? ['_LT_FAST_ROADS'] : []), ...(ray ? ['SHADER_STAGE_RAY_TRACING'] : [])];
    const macros = new Set(['_LT_FAST_LAYERS', '_LT_FAST_PREHEIGHT', ...extras]);
    const specialized = selectSource('LTLayerBlendCore.hlsl', macros);
    check(macros.has('LT_FAST_PREHEIGHT_PATH'), 'both keywords enable projected specialization');
    check(!arrayDeclaration.test(specialized), 'specialized core strips generic scratch arrays');
    check(!/\bLTStrongestLayerPair\s*\(/.test(specialized), 'specialized core strips generic ranking helper');
    check(!specialized.includes('LTBlendPairRemainderColor'), 'unchecked pair variant strips remainder code entirely');
    const planar = functionBody(specialized, 'LTSampleLayersMappedFrameWithCoverage');
    check(/\bLTPrepareLayerPair\s*\(/.test(planar) && /\bLTSampleLayerPair\s*\(/.test(planar), 'planar entry reaches pair implementation');
    const projected = functionBody(selectSource('LTProjectedLayers.hlsl', macros), 'LTSampleProjectedLayersGrad');
    const coverage = projected.search(/\bLTReadLayerCoverage\s*\(/), preparation = projected.search(/\bLTPrepareLayerPair\s*\(/);
    const axisLoop = projected.search(/\bfor\s*\(\s*int\s+axis\b/), sampling = projected.search(/\bLTSampleLayerPair\s*\(/);
    check(coverage >= 0 && coverage < preparation && preparation < axisLoop && axisLoop < sampling,
        'projected path reads coverage and prepares pair before axes');
    check((projected.match(/\bLTPrepareLayerPair\s*\(/g) || []).length === 1, 'projected path prepares exactly once');
    check(!projected.includes('LTBlendPairRemainderColor'), 'unchecked projected path has no remainder call');
    const colorMacros = new Set(['_LT_FAST_LAYERS', '_LT_FAST_PREHEIGHT', '_LT_FAST_COLOR_REMAINDER', ...extras]);
    const colored = selectSource('LTLayerBlendCore.hlsl', colorMacros);
    const remainder = functionBody(colored, 'LTBlendPairRemainderColor');
    check(!arrayDeclaration.test(colored), 'color-only variant retains no indexable scratch arrays');
    check(remainder.includes('_LTColorArray') && !/_LT(?:Normal|Mask)Array|LTReadPairMask|LTApplyAsphaltCracks/.test(remainder), 'remainder samples albedo only, no masks/normals/cracks');
    check(remainder.includes('slot==first.slot||slot==second.slot'), 'remainder never resamples either main layer');
    check(remainder.includes('if(raw<=.00001)continue'), 'zero/tiny remainder skips texture sampling');
    check(remainder.includes('if(remainderMass>0)color='), 'no remainder is exact no-op');
    check(remainder.includes('_LTFastLayerParams.w<1.5'), 'per-tile adapter flag isolates strict/colored modes');
    check(remainder.includes('LTRoadMappedUV')===roads, 'optional color remainder preserves road coordinates');
    check(functionBody(colored, 'LTSampleLayersMappedFrameWithCoverage').includes('LTBlendPairRemainderColor'), 'planar caller includes optional color');
    check(functionBody(selectSource('LTProjectedLayers.hlsl', colorMacros), 'LTSampleProjectedLayersGrad').includes('LTBlendPairRemainderColor'), 'projected caller includes optional color');
    const genericMacros = new Set(['_LT_FAST_LAYERS', ...extras]);
    const generic = selectSource('LTLayerBlendCore.hlsl', genericMacros);
    check(arrayDeclaration.test(functionBody(generic, 'LTSampleLayersMappedFrameWithCoverage')), 'fast mode without specialization retains generic path');
    check(!genericMacros.has('LT_FAST_PREHEIGHT_PATH'), 'generic fast mode does not select projected pair');
    const ordinary = selectSource('LTLayerBlendCore.hlsl', new Set(extras));
    const strayMacros = new Set(['_LT_FAST_PREHEIGHT', ...extras]);
    checks++; assert.equal(selectSource('LTLayerBlendCore.hlsl', strayMacros), ordinary, 'preheight keyword alone cannot change ordinary sampler');
    check(!strayMacros.has('LT_FAST_PREHEIGHT_PATH'), 'preheight keyword requires fast layers');
}

console.log(`PASS ${cases} CPU pair/oracle comparisons across three projections and double/float32 arithmetic; ${checks} assertions.`);
console.log('Covers zeros, sampling/height thresholds, tiny defaults, slots 8-11, masks/normals, height gain/bias, ties/permutations, and seeded inputs.');
console.log('PASS focused source contracts: two records, no specialized scratch arrays, planar routing, pair before axes, and keyword isolation.');
console.log('CPU/source checks only; not HLSL compilation, Unity import, GPU execution, road/crack rendering, or performance validation.');
