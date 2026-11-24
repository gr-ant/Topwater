using Topwater;

namespace Topwater.Tests;

/// <summary>Bash arrays; every expected value is what real bash prints.</summary>
public class ArrayTests
{
    [Theory]
    // indexed arrays
    [InlineData("a=(x 'y z' w); echo ${#a[@]} \"${a[1]}\" $a", "3 y z x\n")]
    [InlineData("a=(x 'y z'); for e in \"${a[@]}\"; do echo \"[$e]\"; done", "[x]\n[y z]\n")]
    [InlineData("a=(x 'y z'); for e in ${a[@]}; do echo \"[$e]\"; done", "[x]\n[y]\n[z]\n")]
    [InlineData("a=(x 'y z'); printf '%s|' \"${a[*]}\"; echo", "x y z|\n")]
    [InlineData("a=(); echo ${#a[@]}; for e in \"${a[@]}\"; do echo no; done; echo end", "0\nend\n")]
    [InlineData("a=(1 2); a+=(3 4); a+=(5); echo \"${a[@]}\"", "1 2 3 4 5\n")]
    [InlineData("a=(); for i in 1 2 3; do a+=(\"n $i\"); done; echo ${#a[@]} \"${a[2]}\"", "3 n 3\n")]
    [InlineData("a[2]=c; a[0]=a; echo \"${a[@]}\" ${#a[@]} \"${!a[@]}\"", "a c 2 0 2\n")]
    [InlineData("a=(a b c d); echo \"${a[-1]}\" \"${a[@]:1:2}\" \"${a[@]:2}\"", "d b c c d\n")]
    [InlineData("a=(a b c); i=1; echo ${a[i]} ${a[$i]} ${a[i+1]} ${a[${#a[@]}-1]}", "b b c c\n")]
    [InlineData("a=(a b c); for i in \"${!a[@]}\"; do echo \"$i=${a[$i]}\"; done", "0=a\n1=b\n2=c\n")]
    [InlineData("a=(a b c); for ((i=0; i<${#a[@]}; i++)); do printf '%s' \"${a[i]}\"; done; echo", "abc\n")]
    [InlineData("a=(a b c); unset 'a[1]'; echo \"${a[@]}\" ${#a[@]}; unset a; echo \"[${a[@]}]\"", "a c 2\n[]\n")]
    [InlineData("a=(one two); a[1]=TWO; a[5]=six; echo \"${a[@]}\"; echo ${#a[1]}", "one TWO six\n3\n")]
    [InlineData("a=(a.txt b.txt); echo \"${a[@]%.txt}\" \"${a[@]/#/x-}\" \"${a[@]^^}\"", "a b x-a.txt x-b.txt A.TXT B.TXT\n")]
    [InlineData("a=($(echo one two three)); echo ${#a[@]} ${a[2]}", "3 three\n")]
    [InlineData("touch f1.log f2.log; a=(*.log); echo ${#a[@]} \"${a[@]}\"", "2 f1.log f2.log\n")]
    [InlineData("a=(\n  one   # comment\n  \"two words\"\n)\necho ${#a[@]} \"${a[1]}\"", "2 two words\n")]
    [InlineData("a=(1 2 3); b=(\"${a[@]}\" 4); echo \"${b[@]}\"; c=(\"${a[@]:1}\"); echo \"${c[@]}\"", "1 2 3 4\n2 3\n")]
    [InlineData("a=(3 4); echo $(( a[0] + a[1] )) $(( ${a[0]} * 2 ))", "7 6\n")]
    [InlineData("a=(1 2); (( a[0]++ )); (( a[1] += 10 )); echo \"${a[@]}\"", "2 12\n")]
    [InlineData("a=(x); echo \"${a[5]:-none}\" \"${a[0]:-none}\" \"${a[5]+set}|\"", "none x |\n")]
    [InlineData("x=scalar; echo \"${x[0]}\" \"${x[@]}\" ${#x[@]}", "scalar scalar 1\n")]
    // associative arrays
    [InlineData("declare -A m; m[one]=1; m[\"two words\"]=2; echo \"${m[one]}\" \"${m[two words]}\" ${#m[@]}", "1 2 2\n")]
    [InlineData("declare -A m=([a]=1 [b]=2); for k in \"${!m[@]}\"; do echo \"$k=${m[$k]}\"; done | sort", "a=1\nb=2\n")]
    [InlineData("declare -A c; for w in a b a; do (( c[$w]++ )); done; echo ${c[a]} ${c[b]}", "2 1\n")]
    [InlineData("declare -A m; k=key; m[$k]=v; [[ -n \"${m[$k]+x}\" ]] && echo has; [[ -z \"${m[nope]+x}\" ]] && echo not", "has\nnot\n")]
    [InlineData("declare -A m=([a]=1); unset 'm[a]'; echo ${#m[@]}", "0\n")]
    // built-ins that fill arrays
    [InlineData("IFS=, read -ra parts <<< 'a,b c,d'; echo ${#parts[@]} \"${parts[1]}\"", "3 b c\n")]
    [InlineData("printf 'l1\\nl 2\\n' > f; mapfile -t lines < f; echo ${#lines[@]} \"${lines[1]}\"", "2 l 2\n")]
    [InlineData("readarray -t lines < <(printf 'a\\nb\\n'); echo \"${lines[@]}\"", "a b\n")]
    // scope
    [InlineData("a=(outer); f(){ local a=(inner more); echo ${#a[@]}; }; f; echo \"${a[@]}\"", "2\nouter\n")]
    [InlineData("f(){ local -a acc; acc+=(x); acc+=(y); echo \"${acc[@]}\"; }; f; echo \"[${acc[@]}]\"", "x y\n[]\n")]
    [InlineData("a=(1); (a+=(2); echo ${#a[@]}); echo ${#a[@]}", "2\n1\n")]
    [InlineData("a=(1 2); echo \"${a[@]}\" | (read x y; echo $y); echo ${#a[@]}", "2\n2\n")]
    public async Task MatchesBash(string cmd, string expected)
    {
        var r = await new Shell().RunAsync(cmd);
        Assert.True(r.Stderr == "", r.Stderr);
        Assert.Equal(expected, r.Stdout);
    }
}

public class ArrayEdgeTests
{
    [Theory]
    [InlineData("a=('x y' z); IFS=,; echo \"${a[*]}\"; unset IFS", "x y,z\n")]
    [InlineData("list=(3 1 2); IFS=$'\\n' sorted=($(sort <<<\"${list[*]}\")); unset IFS; echo \"${sorted[@]}\"", "1 2 3\n")]
    [InlineData("a=(a b c); unset 'a[0]'; a=(\"${a[@]}\"); echo \"${!a[@]}\"", "0 1\n")]
    [InlineData("args=(-l -a); set -- \"${args[@]}\" extra; echo $# \"$2\"", "3 -a\n")]
    [InlineData("f() { local -a r=(\"$@\"); echo \"${#r[@]}:${r[1]}\"; }; f p 'q r' s", "3:q r\n")]
    [InlineData("x=a; echo ${x/#/pre-} ${x/%/-post}", "pre-a a-post\n")]
    public async Task MatchesBash(string cmd, string expected)
    {
        var r = await new Shell().RunAsync(cmd);
        Assert.True(r.Stderr == "", r.Stderr);
        Assert.Equal(expected, r.Stdout);
    }
}
