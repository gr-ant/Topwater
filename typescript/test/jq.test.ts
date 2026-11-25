import { describe, expect, it } from 'vitest';
import { makeEnv } from './harness.js';
import { registerJq } from '../src/commands/jq.js';
import { LimitExceeded } from '../src/command.js';

const { fs, shell, run } = makeEnv();
registerJq(shell as any);

const jq = (args: string[], input = '', deadlineMs = 10_000) => run(['jq', ...args], input, deadlineMs);

// [filter, input, expected stdout lines], run with -c
const cases: [string, string, string][] = [
  ['.', '{"a":1}', '{"a":1}'],
  ['.a', '{"a":1}', '1'],
  ['.a.b', '{"a":{"b":2}}', '2'],
  ['.a?', '1', ''],
  ['.["a"]', '{"a":1}', '1'],
  ['."a"', '{"a":1}', '1'],
  ['.[0], .[-1]', '[1,2,3]', '1\n3'],
  ['.[1:]', '[1,2,3]', '[2,3]'],
  ['.[:2]', '"abcd"', '"ab"'],
  ['.[]', '[1,[2]]', '1\n[2]'],
  ['.[]?', '1', ''],
  ['..', '[[1]]', '[[1]]\n[1]\n1'],
  ['[.[] | . * 2]', '[1,2]', '[2,4]'],
  ['map(.+1)', '[1,2]', '[2,3]'],
  ['map_values(.+1)', '{"a":1}', '{"a":2}'],
  ['{a, b: .c}', '{"a":1,"c":2}', '{"a":1,"b":2}'],
  ['{(.k): 1}', '{"k":"x"}', '{"x":1}'],
  ['{"a b": 1}', 'null', '{"a b":1}'],
  ['{a: (1,2)}', 'null', '{"a":1}\n{"a":2}'],
  ['"a\\(.b)c"', '{"b":1}', '"a1c"'],
  ['@base64 "x\\(.)"', '"hi"', '"xaGk="'],
  ['1 + 2 * 3 - 4 / 2', 'null', '5'],
  ['10 % 3, -10 % 3, 5 % -2', 'null', '1\n-1\n1'],
  ['[1,2] + [3]', 'null', '[1,2,3]'],
  ['{"a":1} + {"b":2}', 'null', '{"a":1,"b":2}'],
  ['{"a":{"b":1}} * {"a":{"c":2}}', 'null', '{"a":{"b":1,"c":2}}'],
  ['"ab" * 3', 'null', '"ababab"'],
  ['"a,b" / ","', 'null', '["a","b"]'],
  ['[1,2,3] - [2]', 'null', '[1,3]'],
  ['null + 1', 'null', '1'],
  ['1 == 1.0, "a" < "b", [1] < [2], null < false, {} > []', 'null', 'true\ntrue\ntrue\ntrue\ntrue'],
  ['1 and null, 1 or error, (null, false) or true', 'null', 'false\ntrue\ntrue\ntrue'],
  ['.a // "d"', '{"a":null}', '"d"'],
  ['(false, null, 1) // 2', 'null', '1'],
  ['empty // 3', 'null', '3'],
  ['if . > 1 then "big" elif . == 1 then "one" else "small" end', '1', '"one"'],
  ['if . then 1 end', 'false', 'false'],
  ['try error("x") catch .', 'null', '"x"'],
  ['try (1, error("x"), 3) catch .', 'null', '1\n"x"'],
  ['[.[] | try tonumber catch "bad"]', '["1","x"]', '[1,"bad"]'],
  ['[.[] | tonumber?]', '["1","x","3"]', '[1,3]'],
  ['error(null)?', 'null', ''],
  ['. as $x | $x + 1', '1', '2'],
  ['. as [$a, $b] | $a + $b', '[1,2]', '3'],
  ['. as {a: $x, b: [$y]} | [$x, $y]', '{"a":1,"b":[2]}', '[1,2]'],
  ['.[] as {$a} | $a', '[{"a":1},{"a":2}]', '1\n2'],
  ['reduce .[] as $x (0; . + $x)', '[1,2,3]', '6'],
  ['reduce .[] as [$a,$b] (0; . + $a * $b)', '[[1,2],[3,4]]', '14'],
  ['[foreach .[] as $x (0; . + $x)]', '[1,2,3]', '[1,3,6]'],
  ['[foreach .[] as $x (0; . + $x; [$x, .])]', '[1,2]', '[[1,1],[2,3]]'],
  ['def f(x): x * 2; f(.)', '3', '6'],
  ['def f($a; $b): $a + $b; f(1; 2)', 'null', '3'],
  ['def fac: if . <= 1 then 1 else . * (. - 1 | fac) end; fac', '10', '3628800'],
  ['def f: def g: 3; g * 2; f', 'null', '6'],
  ['[limit(3; range(10))]', 'null', '[0,1,2]'],
  ['first(range(5;10)), last(range(5)), nth(2; range(5))', 'null', '5\n4\n2'],
  ['[range(0; 10; 3)]', 'null', '[0,3,6,9]'],
  ['[range(5; 0; -2)]', 'null', '[5,3,1]'],
  ['[range(0,1; 3,4)]', 'null', '[0,1,2,0,1,2,3,1,2,1,2,3]'],
  ['[.[] | select(. > 1)]', '[1,2,3]', '[2,3]'],
  ['[1|recurse(if . < 4 then .+1 else empty end)]', 'null', '[1,2,3,4]'],
  ['[recurse(.[]?; . != 2)]', '[1,2]', '[[1,2],1]'],
  ['[1 | while(. < 20; . * 2)]', 'null', '[1,2,4,8,16]'],
  ['1 | until(. > 100; . * 2)', 'null', '128'],
  ['[limit(5; 1 | repeat(. * 2))]', 'null', '[1,2,4,8,16]'],
  ['length', '[1,2]', '2'],
  ['[.[] | length]', '["héllo", {"a":1}, null, -5, [1]]', '[5,1,0,5,1]'],
  ['utf8bytelength', '"héllo"', '6'],
  ['keys, keys_unsorted', '{"b":1,"a":2}', '["a","b"]\n["b","a"]'],
  ['to_entries', '{"a":1}', '[{"key":"a","value":1}]'],
  ['from_entries', '[{"key":"a","value":1},{"name":"b","v":2},{"k":"c"}]', '{"a":1,"b":2,"c":null}'],
  ['with_entries(.value += 1)', '{"a":1,"b":2}', '{"a":2,"b":3}'],
  ['with_entries(select(.key != "a"))', '{"a":1,"b":2}', '{"b":2}'],
  ['add', '[1,2,3]', '6'],
  ['add', '["a","b"]', '"ab"'],
  ['add', '[]', 'null'],
  ['add(.[] | .a)', '[{"a":1},{"a":2}]', '3'],
  ['any, all', '[true,false]', 'true\nfalse'],
  ['any(. > 2), all(. > 0)', '[1,2,3]', 'true\ntrue'],
  ['flatten, flatten(1)', '[1,[2,[3]]]', '[1,2,3]\n[1,2,[3]]'],
  ['floor, ceil, round, sqrt', '2.5', '2\n3\n3\n1.5811388300841898'],
  ['pow(2; 10), log2', '8', '1024\n3'],
  ['tostring, tojson', '[1,"a"]', '"[1,\\"a\\"]"\n"[1,\\"a\\"]"'],
  ['tostring', '"a"', '"a"'],
  ['tonumber', '"1.5"', '1.5'],
  ['fromjson', '"{\\"a\\":[1]}"', '{"a":[1]}'],
  ['type', '[]', '"array"'],
  ['[.[] | type]', '[null,true,1,"a",[],{}]', '["null","boolean","number","string","array","object"]'],
  ['sort', '[3,1,2]', '[1,2,3]'],
  ['sort', '[{"a":1},[1],"a",1,true,false,null]', '[null,false,true,1,"a",[1],{"a":1}]'],
  ['sort_by(.a)', '[{"a":2},{"a":1}]', '[{"a":1},{"a":2}]'],
  ['sort_by(.a, .b)', '[{"a":1,"b":2},{"a":1,"b":1}]', '[{"a":1,"b":1},{"a":1,"b":2}]'],
  ['group_by(.a)', '[{"a":1,"b":1},{"a":2},{"a":1,"b":2}]', '[[{"a":1,"b":1},{"a":1,"b":2}],[{"a":2}]]'],
  ['unique, unique_by(length)', '["a","bb","a","cc"]', '["a","bb","cc"]\n["a","bb"]'],
  ['min, max, min_by(.a), max_by(.a)', '[{"a":2},{"a":1}]', '{"a":1}\n{"a":2}\n{"a":1}\n{"a":2}'],
  ['min, max', '[]', 'null\nnull'],
  ['reverse', '[1,2]', '[2,1]'],
  ['reverse', '"abc"', '"cba"'],
  ['contains("b"), inside("abc")', '"abc"', 'true\ntrue'],
  ['contains({a:[1]})', '{"a":[1,2],"b":1}', 'true'],
  ['has("a"), has("z")', '{"a":1}', 'true\nfalse'],
  ['has(1), has(5)', '[1,2]', 'true\nfalse'],
  ['"a" | in({"a":1})', 'null', 'true'],
  ['startswith("a"), endswith("c"), ltrimstr("a"), rtrimstr("c")', '"abc"', 'true\ntrue\n"bc"\n"ab"'],
  ['join(",")', '["a",1,null,true]', '"a,1,,true"'],
  ['split(", ")', '"a, b"', '["a","b"]'],
  ['ascii_downcase, ascii_upcase', '"aBé"', '"abé"\n"ABé"'],
  ['explode, (explode | implode)', '"aé"', '[97,233]\n"aé"'],
  ['trim, ltrim, rtrim', '"  a "', '"a"\n"a "\n"  a"'],
  ['index("b"), rindex("b"), indices("b")', '"abcb"', '1\n3\n[1,3]'],
  ['indices(1)', '[0,1,2,1]', '[1,3]'],
  ['indices([1,2])', '[0,1,2,1,2]', '[1,3]'],
  ['test("B"; "i"), test("x")', '"abc"', 'true\nfalse'],
  ['match("b+") | [.offset, .length, .string]', '"abbc"', '[1,2,"bb"]'],
  ['[match("a"; "g") | .offset]', '"aXa"', '[0,2]'],
  ['capture("(?<y>\\\\d+)-(?<m>\\\\d+)")', '"2024-05"', '{"y":"2024","m":"05"}'],
  ['[scan("\\\\d+")]', '"a1b22"', '["1","22"]'],
  ['[scan("(a)(b)")]', '"abab"', '[["a","b"],["a","b"]]'],
  ['sub("b"; "X"), gsub("b"; "X")', '"abab"', '"aXab"\n"aXaX"'],
  ['gsub("(?<c>[a-z])"; "\\(.c|ascii_upcase)")', '"ab"', '"AB"'],
  ['gsub(""; "-")', '"abc"', '"-a-b-c-"'],
  ['gsub("\\\\s+"; " ")', '"a  b \\n c"', '"a b c"'],
  ['[splits(", *")]', '"a, b,c"', '["a","b","c"]'],
  ['split("a+"; "g")', '"xaay"', '["x","y"]'],
  ['@csv', '[1,"a\\"b",null,true]', '"1,\\"a\\"\\"b\\",,true"'],
  ['@tsv', '["a\\tb",1]', '"a\\\\tb\\t1"'],
  ['@html', '"<a href=\\"x\\">&\'"', '"&lt;a href=&quot;x&quot;&gt;&amp;&#39;"'],
  ['@uri', '"a b/é"', '"a%20b%2F%C3%A9"'],
  ['@sh', '"it\'s"', '"\'it\'\\\\\'\'s\'"'],
  ['@sh', '["a b",1]', '"\'a b\' 1"'],
  ['@base64, (@base64 | @base64d)', '"héllo"', '"aMOpbGxv"\n"héllo"'],
  ['@json', '[1,"a"]', '"[1,\\"a\\"]"'],
  ['@text', '[1]', '"[1]"'],
  ['paths', '{"a":[1]}', '["a"]\n["a",0]'],
  ['[paths(type == "number")]', '{"a":[1],"b":2}', '[["a",0],["b"]]'],
  ['[leaf_paths]', '{"a":[1],"b":2}', '[["a",0],["b"]]'],
  ['path(.a[0].b)', 'null', '["a",0,"b"]'],
  ['[path(..)]', '[[1]]', '[[],[0],[0,0]]'],
  ['getpath(["a","b"])', '{"a":{"b":5}}', '5'],
  ['getpath(["x","y"])', '{}', 'null'],
  ['setpath(["a","b"]; 1)', 'null', '{"a":{"b":1}}'],
  ['delpaths([["a"],["b",0]])', '{"a":1,"b":[1,2]}', '{"b":[2]}'],
  ['del(.a)', '{"a":1,"b":2}', '{"b":2}'],
  ['del(.[0,2])', '[1,2,3]', '[2]'],
  ['del(.[] | select(. > 1))', '[1,2,3]', '[1]'],
  ['del(..|select(. == null))', '{"a":null,"b":[null,1]}', '{"b":[1]}'],
  ['.a = 1', '{}', '{"a":1}'],
  ['.a.b.c = 1', 'null', '{"a":{"b":{"c":1}}}'],
  ['.[2] = 1', '[]', '[null,null,1]'],
  ['.a |= . + 1', '{"a":1}', '{"a":2}'],
  ['.[] |= . * 2', '[1,2]', '[2,4]'],
  ['.a += 1, .a -= 1, .a *= 3, .a /= 2, .a %= 3', '{"a":4}', '{"a":5}\n{"a":3}\n{"a":12}\n{"a":2}\n{"a":1}'],
  ['.a //= 5', '{"a":null}', '{"a":5}'],
  ['.a = (.b, .c)', '{"b":1,"c":2}', '{"b":1,"c":2,"a":1}\n{"b":1,"c":2,"a":2}'],
  ['.b = .a', '{"a":1}', '{"a":1,"b":1}'],
  ['(.a, .b) = 9', '{}', '{"a":9,"b":9}'],
  ['.[] |= empty', '[1,2,3]', '[]'],
  ['(.[] | select(. >= 2)) |= empty', '[1,5,3,0,7]', '[1,0]'],
  ['.a[1:] = ["x"]', '{"a":[1,2,3]}', '{"a":[1,"x"]}'],
  ['to_entries | map(select(.value > 1)) | from_entries', '{"a":1,"b":2}', '{"b":2}'],
  ['walk(if type == "number" then . + 1 else . end)', '[1,{"a":2}]', '[2,{"a":3}]'],
  ['transpose', '[[1,2],[3]]', '[[1,3],[2,null]]'],
  ['[combinations]', '[[1,2],[3,4]]', '[[1,3],[1,4],[2,3],[2,4]]'],
  ['[.[] | tostring]', '[1,"a",null]', '["1","a","null"]'],
  ['bsearch(2), bsearch(5)', '[1,2,3]', '1\n-4'],
  ['[tostream]', '{"a":[1,2]}', '[[["a",0],1],[["a",1],2],[["a",1]],[["a"]]]'],
  ['fromstream(tostream)', '{"a":[1,{"b":2}]}', '{"a":[1,{"b":2}]}'],
  ['[1|truncate_stream([[0],1],[[1,0],2],[[1,0]],[[1]])]', 'null', '[[[0],2],[[0]]]'],
  ['pick(.a, .b.c)', '{"a":1,"b":{"c":2,"d":3},"e":4}', '{"a":1,"b":{"c":2}}'],
  ['INDEX(.id)', '[{"id":1},{"id":2}]', '{"1":{"id":1},"2":{"id":2}}'],
  ['.[] | IN(1,2)', '[1,3]', 'true\nfalse'],
  ['isempty(empty), isempty(1)', 'null', 'true\nfalse'],
  ['[.[] | numbers, strings]', '[1,"a",null]', '[1,"a"]'],
  ['[.[] | values]', '[1,null]', '[1]'],
  ['label $out | 1, 2, break $out, 3', 'null', '1\n2'],
  ['[label $f | range(10) | ., (select(. == 3) | break $f)]', 'null', '[0,1,2,3]'],
  ['$__loc__', 'null', '{"file":"<top-level>","line":1}'],
  ['tojson | fromjson', '{"a":[1,2.5]}', '{"a":[1,2.5]}'],
  ['.[] as $x | $x * 2', '[1,2]', '2\n4'],
  ['{a: 1} | .a as $x | {b: $x}', 'null', '{"b":1}'],
  ['[.[] | if . > 1 then "y" else "n" end]', '[1,2]', '["n","y"]'],
  ['ascii', '65', '"A"'],
  ['todate', '1425599621', '"2015-03-05T23:53:41Z"'],
  ['fromdate', '"2015-03-05T23:53:41Z"', '1425599621'],
  ['strftime("%Y-%m-%d %H:%M:%S %a %b %j")', '1425599621', '"2015-03-05 23:53:41 Thu Mar 064"'],
  ['gmtime | mktime', '1425599621', '1425599621'],
  ['strptime("%Y-%m-%dT%H:%M:%SZ") | mktime', '"2015-03-05T23:53:41Z"', '1425599621'],
  ['[.[]|tojson]', '[1.0, 1.5, 100, 1e3]', '["1","1.5","100","1000"]'],
  ['limit(0; 1,2)', 'null', ''],
  ['[limit(-1; 1,2)]', 'null', '[1,2]'],
  ['input', '1 2', '2'],
  ['[., input]', '1 2 3 4', '[1,2]\n[3,4]'],
  ['@json "x\\(.)"', '[1]', '"x[1]"'],
  ['env | type', 'null', '"object"'],
  ['$ENV | type', 'null', '"object"'],
  ['[.[] | abs]', '[-1, 2]', '[1,2]'],
  ['getpath(["a"]) |= 5', '{}', '{"a":5}'],
  ['.. |= (if type == "number" then . + 1 else . end)', '[1,[2]]', '[2,[3]]'],
  ['.a as $x | .b as $y | $x + $y', '{"a":1,"b":2}', '3'],
  ['try (null | implode) catch "e"', 'null', '"e"'],
  ['"abc" | .[1:2]', 'null', '"b"'],
  ['"aé😀b" | length, .[2:3], (explode|length)', 'null', '4\n"😀"\n4'],
  ['"😀" | test("^.$")', 'null', 'true'],
  ['[.[] | -.]', '[1,-2]', '[-1,2]'],
  ['0 - (1+2), (1 | -(1+2))', 'null', '-3\n-3'],
  ['.a.b |= 3', '{"a":null}', '{"a":{"b":3}}'],
  ['(1,2) as $x | $x', 'null', '1\n2'],
  ['[.[] | select(.a == 1 or .b == 2)] | length', '[{"a":1},{"b":2},{"c":3}]', '2'],
  ['map(select(.n | test("^a")))', '[{"n":"ab"},{"n":"ba"}]', '[{"n":"ab"}]'],
  ['.a // .b // "none"', '{}', '"none"'],
  ['first, last, nth(1)', '[1,2,3]', '1\n3\n2'],
  ['[.[]|.a?]', '[1,{"a":2}]', '[2]'],
  ['.a[]?', '{"a":1}', ''],
  ['[..|numbers]', '{"a":[1,{"b":2}]}', '[1,2]'],
  ['map(has("a"))', '[{"a":1},{}]', '[true,false]'],
  ['(.[] | select(type == "number")) |= . + 1', '[1,"a"]', '[2,"a"]'],
  ['(.a | select(. > 0)) = 5', '{"a":1}', '{"a":5}'],
  ['{a:1} | to_entries[] | "\\(.key)=\\(.value)"', 'null', '"a=1"'],
  ['@sh "echo \\(.)"', '"a b"', '"echo \'a b\'"'],
  ['def addvalue(f): f as $x | map(. + $x); addvalue(.[0])', '[[1,2],[10,20]]', '[[1,2,1,2],[10,20,1,2]]'],
  ['def f(g): [g]; f(1,2)', 'null', '[1,2]'],
  ['def fib: if . < 2 then . else (. - 1 | fib) + (. - 2 | fib) end; [range(10) | fib]', 'null', '[0,1,1,2,3,5,8,13,21,34]'],
  ['[range(5)] | .[2:4] = []', 'null', '[0,1,4]'],
  ['ascii_downcase', '"ÀB"', '"Àb"'],
  ['"\\u00e9\\ud83d\\ude00"', 'null', '"é😀"'],
  ['[.[] | round]', '[0.5, 1.5, -0.5, -1.5, 2.4]', '[1,2,-1,-2,2]'],
  ['1e1000, -1e1000', 'null', '1.7976931348623157e+308\n-1.7976931348623157e+308'],
  ['[nan] | .[0] != .[0], (nan < 1), tojson', 'null', 'true\ntrue\n"[null]"'],
  ['infinite | tostring', 'null', '"1.7976931348623157e+308"'],
  ['.a = 1 // 2', '{}', '{"a":1}'],
  ['(1 as $x | 2 as $y | [$x,$y,$__loc__.line])', 'null', '[1,2,1]'],
  ['[.[] as [$a] | $a]', '[[1],[2]]', '[1,2]'],
  ['reduce range(5) as $i ([]; . + [$i * 2])', 'null', '[0,2,4,6,8]'],
  ['[range(3)] | map(. as $x | $x * $x)', 'null', '[0,1,4]'],
  // reduce updates the accumulator in place only when nothing else can see it
  ['reduce range(3) as $i ({}; .x = [.])', 'null', '{"x":[{"x":[{"x":[{}]}]}]}'],
  ['reduce range(3) as $i ({}; .a = .)', 'null', '{"a":{"a":{"a":{}}}}'],
  ['{"k":1} as $o | reduce range(2) as $i ($o; .z = $i) | [., $o]', 'null', '[{"k":1,"z":1},{"k":1}]'],
  ['reduce .[] as $w ({}; .[$w] += 1)', '["a","b","a"]', '{"a":2,"b":1}'],
  ['reduce range(3) as $i ([]; .[$i] = $i * 2)', 'null', '[0,2,4]'],
  ['reduce range(4) as $i ({}; .["k\\($i)"] |= (if $i % 2 == 0 then empty else $i end))', 'null', '{"k1":1,"k3":3}'],
  ['reduce range(2) as $i ({a:{b:1}}; .c = .a | .a.b = $i)', 'null', '{"a":{"b":1},"c":{"b":0}}'],
  ['reduce .[] as {k: $k, v: $v} ({}; .[$k] = $v)', '[{"k":"a","v":1},{"k":"b","v":2}]', '{"a":1,"b":2}'],
  ['(first(.[] | select(. > 1))) |= 10', '[1,2,3]', '[1,10,3]'],
  ['sort_by(.n) | map(.n)', '[{"n":"b"},{"n":"a"},{"n":"é"},{"n":"😀"},{"n":"\\uffff"}]', '["a","b","é","\uffff","😀"]'],
  ['[.[] | select(.a? // false)]', '[{"a":1},{"b":1},3]', '[{"a":1}]'],
  ['.[] | select(.id == (1,2)) | .id', '[{"id":1},{"id":3},{"id":2}]', '1\n2'],
  ['[paths(..)]|length', '[[1]]', '3'],
  ['to_entries | map("\\(.key)=\\(.value)") | join("&")', '{"a":1,"b":"x"}', '"a=1&b=x"'],
  ['[.[] | (.a, .b) = 1]', '[{}]', '[{"a":1,"b":1}]'],
  ['.. | select(type == "string")', '{"a":["x",{"b":"y"}]}', '"x"\n"y"'],
  ['map(select(.tags | index("a")))', '[{"tags":["a","b"]},{"tags":["c"]}]', '[{"tags":["a","b"]}]'],
  ['walk(if type == "object" then del(.x) else . end)', '{"x":1,"a":[{"x":2,"y":3}]}', '{"a":[{"y":3}]}'],
  ['[splits("[,;]")]', '"a,b;c"', '["a","b","c"]'],
  ['ascii_downcase | test("^[a-z]+$")', '"ABC"', 'true'],
  ['capture("(?<a>x)?(?<b>y)")', '"y"', '{"a":null,"b":"y"}'],
  ['[match("(a)|(b)"; "g") | .captures | map(.string)]', '"ab"', '[["a",null],[null,"b"]]'],
  ['test("a.c"; "x"), test("a c"; "x")', '"abc"', 'true\nfalse'],
  ['[.[] | tostring | ltrimstr("0")]', '[0, 5]', '["","5"]'],
  ['@csv "\\(.)"', '[1,2]', '"1,2"'],
  ['tojson, (tojson | fromjson)', '{"a":"é\\n"}', '"{\\"a\\":\\"é\\\\n\\"}"\n{"a":"é\\n"}'],
  ['limit(2; .[])', '[1,2,3]', '1\n2'],
  ['[.[] | floor]', '[1.9, -1.1]', '[1,-2]'],
  ['getpath(["a",0,"b"])', '{"a":[{"b":1}]}', '1'],
  ['[.[] | select(. != null)] | length', '[null,1,false]', '2'],
];

describe('jq programs', () => {
  for (const [filter, input, expected] of cases) {
    it(`${filter}  <<  ${input}`, async () => {
      const r = await jq(['-c', filter], input);
      expect(r.stderr).toBe('');
      expect(r.stdout).toBe(expected === '' ? '' : expected + '\n');
      expect(r.exitCode).toBe(0);
    });
  }
});

describe('jq numbers', () => {
  it.each([
    ['0.1 + 0.2', '0.30000000000000004'], ['1e-5', '1e-05'], ['3/2', '1.5'], ['1e17', '1e+17'], ['100000', '100000'],
    ['1.0', '1'], ['0.0001', '0.0001'], ['0 * -1', '-0'], ['123456789012', '123456789012'], ['1.5e300', '1.5e+300'],
    ['3.14159', '3.14159'], ['1e15', '1000000000000000'], ['12345678901234567890', '12345678901234567000'],
  ])('%s prints as %s', async (f, e) => { expect((await jq(['-n', f])).stdout).toBe(e + '\n'); });
});

describe('jq output options', () => {
  it('pretty prints with two spaces by default', async () => {
    expect((await jq(['.'], '{"a":[1,{"b":null}],"c":{},"d":[]}')).stdout).toBe('{\n  "a": [\n    1,\n    {\n      "b": null\n    }\n  ],\n  "c": {},\n  "d": []\n}\n');
  });
  it('-c is compact, --tab and --indent change the indent', async () => {
    expect((await jq(['-c', '.'], '{"a": [1, 2]}')).stdout).toBe('{"a":[1,2]}\n');
    expect((await jq(['--tab', '.'], '[1]')).stdout).toBe('[\n\t1\n]\n');
    expect((await jq(['--indent', '1', '.'], '[1]')).stdout).toBe('[\n 1\n]\n');
    expect((await jq(['--indent', '0', '.'], '[1]')).stdout).toBe('[1]\n');
    expect((await jq(['--indent', '9', '.'], '[1]')).exitCode).toBe(2);
  });
  it('-r and -j print raw strings; -a escapes non-ASCII; -S sorts keys', async () => {
    expect((await jq(['-r', '.[]'], '["a\\nb",1]')).stdout).toBe('a\nb\n1\n');
    expect((await jq(['-j', '.[]'], '["a","b",1]')).stdout).toBe('ab1');
    expect((await jq(['-a', '.'], '"é😀"')).stdout).toBe('"\\u00e9\\ud83d\\ude00"\n');
    expect((await jq(['-cS', '.'], '{"b":1,"a":{"d":1,"c":2}}')).stdout).toBe('{"a":{"c":2,"d":1},"b":1}\n');
    expect((await jq(['-c', 'keys_unsorted'], '{"b":1,"a":2,"1":3}')).stdout).toBe('["b","a","1"]\n');
  });
  it('control characters and DEL are escaped', async () => {
    expect((await jq(['.'], '"\\u0001\\u007f\\t"')).stdout).toBe('"\\u0001\\u007f\\t"\n');
  });
  it('-n, -s, -R and combinations', async () => {
    expect((await jq(['-n', '1+1'])).stdout).toBe('2\n');
    expect((await jq(['-c', '-s', '.'], '1 2 3')).stdout).toBe('[1,2,3]\n');
    expect((await jq(['-R', '.'], 'a\nb\n')).stdout).toBe('"a"\n"b"\n');
    expect((await jq(['-Rs', '.'], 'a\nb\n')).stdout).toBe('"a\\nb\\n"\n');
    expect((await jq(['-nR', '[inputs]', '-c'], 'a\nb\n')).stdout).toBe('["a","b"]\n');
    expect((await jq(['-n', '-c', '[inputs]'], '1 2 3')).stdout).toBe('[1,2,3]\n');
    expect((await jq(['-n', 'input'], '1 2')).stdout).toBe('1\n');
    expect((await jq(['-nr', '"a","b"'])).stdout).toBe('a\nb\n');
  });
  it('multiple documents each run the filter', async () => {
    expect((await jq(['-c', '.a'], '{"a":1} {"a":2}\n{"a":3}')).stdout).toBe('1\n2\n3\n');
  });
  it('--stream emits path/leaf events', async () => {
    expect((await jq(['-c', '--stream', '.'], '{"a":[1]}')).stdout).toBe('[["a",0],1]\n[["a",0]]\n[["a"]]\n');
  });
  it('--arg, --argjson, --args, --jsonargs, $ARGS, $ENV', async () => {
    expect((await jq(['-nc', '--arg', 'a', 'x', '--argjson', 'b', '{"c":1}', '[$a, $b]'])).stdout).toBe('["x",{"c":1}]\n');
    expect((await jq(['-nc', '$ARGS', '--arg', 'a', 'x', '--args', 'p1', 'p2'])).stdout).toBe('{"positional":["p1","p2"],"named":{"a":"x"}}\n');
    expect((await jq(['-nc', '$ARGS.positional', '--jsonargs', '1', '{"a":2}'])).stdout).toBe('[1,{"a":2}]\n');
    expect((await jq(['-n', '$ENV.HOME'])).stdout).toBe('"/home/agent"\n');
    expect((await jq(['-n', '--argjson', 'x', '{bad', '1'])).exitCode).toBe(2);
  });
  it('--slurpfile and --rawfile read files; -f reads the program from a file', async () => {
    fs.writeText('/home/agent/v.json', '{"a":1} {"a":2}');
    fs.writeText('/home/agent/t.txt', 'hello\n');
    fs.writeText('/home/agent/p.jq', '.a + 1\n');
    expect((await jq(['-nc', '--slurpfile', 'v', 'v.json', '$v'])).stdout).toBe('[{"a":1},{"a":2}]\n');
    expect((await jq(['-n', '--rawfile', 't', 't.txt', '$t'])).stdout).toBe('"hello\\n"\n');
    expect((await jq(['-f', 'p.jq'], '{"a":1}')).stdout).toBe('2\n');
  });
  it('reads files, reports missing ones with exit 2, and goes on', async () => {
    fs.writeText('/home/agent/a.json', '{"a":1}');
    expect((await jq(['.a', 'a.json'])).stdout).toBe('1\n');
    const r = await jq(['.a', 'nosuch.json', 'a.json']);
    expect(r.stdout).toBe('1\n');
    expect(r.stderr).toBe('jq: error: Could not open nosuch.json: No such file or directory\n');
    expect(r.exitCode).toBe(2);
  });
  it('input_filename is the current file', async () => {
    fs.writeText('/home/agent/a.json', '{"a":1}');
    expect((await jq(['input_filename', 'a.json'])).stdout).toBe('"a.json"\n');
  });
});

describe('jq errors and exit codes', () => {
  it('runtime errors print to stderr and exit 5, then continue with the next input', async () => {
    const r = await jq(['.a.b'], '{"a":1} {"a":{"b":2}}');
    expect(r.stderr).toBe('jq: error (at <unknown>): Cannot index number with "b"\n');
    expect(r.stdout).toBe('2\n');
    expect(r.exitCode).toBe(5);
  });
  it('error objects and null', async () => {
    expect((await jq(['error'], '{"a":1}')).stderr).toBe('jq: error (at <unknown>) (not a string): {"a":1}\n');
    expect((await jq(['error("boom")'], '1')).stderr).toBe('jq: error (at <unknown>): boom\n');
  });
  it('type errors quote a short excerpt of the value', async () => {
    expect((await jq(['.+1'], '"abc"')).stderr).toBe('jq: error (at <unknown>): string ("abc") and number (1) cannot be added\n');
    expect((await jq(['.+1'], '"abcdefghijklmnop"')).stderr).toBe('jq: error (at <unknown>): string ("abcdefghij...) and number (1) cannot be added\n');
    expect((await jq(['.[]'], '1')).stderr).toBe('jq: error (at <unknown>): Cannot iterate over number (1)\n');
    expect((await jq(['.[]'], 'null')).stderr).toBe('jq: error (at <unknown>): Cannot iterate over null\n');
    expect((await jq(['.a'], '[1]')).stderr).toBe('jq: error (at <unknown>): Cannot index array with "a"\n');
    expect((await jq(['1/0'], 'null')).stderr).toContain('cannot be divided because the divisor is zero');
  });
  it('compile errors exit 3', async () => {
    const r = await jq(['-n', 'foo(1)']);
    expect(r.stderr).toBe('jq: error: foo/1 is not defined\njq: 1 compile error\n');
    expect(r.exitCode).toBe(3);
    expect((await jq(['-n', '.a |'])).exitCode).toBe(3);
    expect((await jq(['-n', '$nope'])).stderr).toContain('$nope is not defined');
    expect((await jq(['-n', '{a:}'])).exitCode).toBe(3);
    expect((await jq(['-n', '"abc'])).exitCode).toBe(3);
    expect((await jq(['-n', 'break $x'])).exitCode).toBe(3);
  });
  it('invalid JSON input exits 2 after the values before it', async () => {
    const r = await jq(['-c', '.'], '{"a":1} {bad');
    expect(r.stdout).toBe('{"a":1}\n');
    expect(r.stderr).toMatch(/^jq: error \(at <stdin>:\d+\): /);
    expect(r.exitCode).toBe(2);
    expect((await jq(['.'], '{"a":')).stderr).toContain('Unfinished JSON term');
    expect((await jq(['.'], '[1,]')).exitCode).toBe(2);
  });
  it('usage errors exit 2', async () => {
    expect((await jq([])).exitCode).toBe(2);
    expect((await jq(['--bogus', '.'])).exitCode).toBe(2);
  });
  it('-e sets the exit status from the last output', async () => {
    expect((await jq(['-e', '.a'], '{"a":1}')).exitCode).toBe(0);
    expect((await jq(['-e', '.a'], '{"a":false}')).exitCode).toBe(1);
    expect((await jq(['-e', '.a'], '{}')).exitCode).toBe(1);
    expect((await jq(['-e', 'empty'], '{}')).exitCode).toBe(4);
    expect((await jq(['-e', '.a > 1'], '{"a":1} {"a":2}')).stdout).toBe('false\ntrue\n');
    expect((await jq(['-e', '.a > 1'], '{"a":1} {"a":2}')).exitCode).toBe(0);
  });
  it('halt and halt_error', async () => {
    expect((await jq(['-n', '1, halt, 2'])).stdout).toBe('1\n');
    const r = await jq(['-n', '"bye\\n" | halt_error(3)']);
    expect(r.stderr).toBe('bye\n');
    expect(r.exitCode).toBe(3);
  });
  it('try/catch keeps going and break passes through try', async () => {
    expect((await jq(['-c', '[label $x | try (1, break $x, 2) catch "no"]'], 'null')).stdout).toBe('[1]\n');
  });
});

describe('jq safety limits', () => {
  it('stops runaway generators after 20M steps', async () => {
    await expect(jq(['-n', '[range(1e10)] | length'], '', 60_000)).rejects.toThrow(LimitExceeded);
    const s = await jq(['-n', 'last(range(1e12))'], '', 60_000);
    expect(s.stderr).toBe('jq: error (at <unknown>): too many steps (possible infinite loop)\n');
    expect(s.exitCode).toBe(5);
  }, 60_000);
  it('stops an endless recurse', async () => {
    await expect(jq(['-n', '[1|recurse(.+1)] | length'], '', 60_000)).rejects.toThrow(LimitExceeded);
  }, 60_000);
  it('values past the string limit raise LimitExceeded', async () => {
    const env = makeEnv();
    registerJq(env.shell as any);
    env.shell.options.maxStringBytes = 1 << 20;
    await expect(env.run(['jq', '-n', 'reduce range(100) as $i ("a"; . + .)'])).rejects.toThrow(LimitExceeded);
    await expect(env.run(['jq', '-n', 'reduce range(100) as $i ([1]; . + .)'])).rejects.toThrow(/jq: value too large \(limit 1 MB\)/);
    await expect(env.run(['jq', '-n', '"ab" * 1e12'])).rejects.toThrow(/too large/);
  });
  it('deep nesting reports an error instead of crashing', async () => {
    const deepFilter = '['.repeat(5000) + '1' + ']'.repeat(5000);
    const a = await jq(['-n', deepFilter]);
    expect(a.exitCode).toBe(3);
    expect(a.stderr).toContain('nested too deeply');
    const deepJson = '['.repeat(5000) + ']'.repeat(5000);
    const b = await jq(['-c', 'length'], deepJson);
    expect(b.exitCode).toBe(2);
    const c = await jq(['-n', 'def f: if . > 100000 then . else (. + 1 | f) end; 0 | f']);
    expect([0, 5]).toContain(c.exitCode);
  }, 60_000);
  it('a deadline ends a long loop', async () => {
    await expect(jq(['-n', 'last(range(1e12))'], '', -1)).rejects.toThrow();
  });
});

describe('jq on lines of agent-style use', () => {
  it('group, count and format', async () => {
    const input = '{"a":null,"items":[{"t":"x"},{"t":"y"},{"t":"x"}]}';
    expect((await jq(['-r', '.a // "none", (.items | group_by(.t) | map("\\(.[0].t)=\\(length)") | join(","))'], input)).stdout).toBe('none\nx=2,y=1\n');
    expect((await jq(['-nc', '--arg', 'x', 'hi', '--argjson', 'n', '5', '{x: $x, n: ($n + 1)} | .n += 1 | del(.x)'])).stdout).toBe('{"n":7}\n');
    expect((await jq(['-nc', '[range(5)] | map(select(. % 2 == 0)) | [first, last, length, (add / length)]'])).stdout).toBe('[0,4,3,2]\n');
    expect((await jq(['-nc', '{a:{b:[1,2]}} | [paths], (.a.b[0] |= . + 10), (try error("boom") catch .), (.x.y // 3)'])).stdout)
      .toBe('[["a"],["a","b"],["a","b",0],["a","b",1]]\n{"a":{"b":[11,2]}}\n"boom"\n3\n');
    expect((await jq(['-nr', '"2024-05" | capture("(?<y>\\\\d+)-(?<m>\\\\d+)") | "\\(.m)/\\(.y)", ([1,"a"] | @csv), ("x y" | @uri)'])).stdout).toBe('05/2024\n1,"a"\nx%20y\n');
    expect((await jq(['-nc', 'reduce (1,2,3) as $i (0; . + $i), (1 | [limit(3; repeat(. * 2))]), ("a-b" | split("-") | to_entries | from_entries)'])).stdout)
      .toBe('6\n[1,2,4]\n{"0":"a","1":"b"}\n');
  });
});
