using Topwater;

namespace Topwater.Tests;

/// <summary>
/// bc compatibility. Every expected value in <see cref="Core"/> is the exact stdout the real GNU bc 1.07.1 on this
/// machine printed for the same program (recorded differentially), so these assert digit-for-digit agreement across
/// arithmetic and the scale rules, ibase/obase (including obase &gt; 16 digit groups), truncation, 70-column line
/// wrapping, the language (functions, arrays, loops, if/else, print, strings, last, ++/--, assignment ops) and the
/// -l math library (s c a l e j at the default scale 20). The math-library values match GNU here; the implementation
/// computes them from series and may differ from GNU in the last digit or two at other scales.
///
/// <see cref="Limits"/> covers the safety caps (these have no real-bc equivalent — GNU would hang or exhaust memory):
/// a runaway or over-large computation stops with an error on stderr and a non-zero exit.
/// </summary>
public class BashCompatBc
{
    [Theory]
    [InlineData("printf '%b\\n' '7/3' | bc", "2\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; 7/3' | bc", "2.33333\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; 7%3' | bc", ".00001\n", 0, true)]
    [InlineData("printf '%b\\n' '2.50+3.250' | bc", "5.750\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=0; 1.5*1.5' | bc", "2.2\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=1; 1.23*4.56' | bc", "5.60\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=10; 1.23*4.56' | bc", "5.6088\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=2; 2.345*3.456' | bc", "8.104\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; 2^10' | bc", "1024\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; 2^-2' | bc", ".25000\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=2; 10^-3' | bc", "0\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; 2.5^3' | bc", "15.625\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; (1/3)^2' | bc", ".11110\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; sqrt(2)' | bc", "1.41421\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; sqrt(2)' | bc", "1.41421356237309504880\n", 0, true)]
    [InlineData("printf '%b\\n' 'sqrt(2.25)' | bc", "1.50\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=10; sqrt(1000000)' | bc", "1000.0000000000\n", 0, true)]
    [InlineData("printf '%b\\n' '-7/2' | bc", "-3\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=2;-7/2' | bc", "-3.50\n", 0, true)]
    [InlineData("printf '%b\\n' '7 % -2' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' '2^200' | bc", "1606938044258990275541962092341162602522202993782792835301376\n", 0, true)]
    [InlineData("printf '%b\\n' '2^300' | bc", "20370359763344860862684456884093781610514683936659362506361404493543\\\n81299763336706183397376\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=80; 1/3' | bc", ".3333333333333333333333333333333333333333333333333333333333333333333\\\n3333333333333\n", 0, true)]
    [InlineData("printf '%b\\n' 'length(12345)' | bc", "5\n", 0, true)]
    [InlineData("printf '%b\\n' 'length(123.45)' | bc", "5\n", 0, true)]
    [InlineData("printf '%b\\n' 'length(0.00123)' | bc", "5\n", 0, true)]
    [InlineData("printf '%b\\n' 'length(0)' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5; scale(1/3)' | bc", "5\n", 0, true)]
    [InlineData("printf '%b\\n' '1==1' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' '5>3' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' '1&&0' | bc", "0\n", 0, true)]
    [InlineData("printf '%b\\n' '1||0' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' '!0' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' '-2^2' | bc", "4\n", 0, true)]
    [InlineData("printf '%b\\n' '2^3^2' | bc", "512\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=16; 255' | bc", "FF\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=16; 4095' | bc", "FFF\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=16; 256' | bc", "100\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=2; 255' | bc", "11111111\n", 0, true)]
    [InlineData("printf '%b\\n' 'ibase=16; FF' | bc", "255\n", 0, true)]
    [InlineData("printf '%b\\n' 'ibase=16; ff' | bc", "0\n", 0, true)]
    [InlineData("printf '%b\\n' 'ibase=2; 1010' | bc", "10\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=16; -255' | bc", "-FF\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=16; 3.5' | bc", "3.8\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=100; 255' | bc", " 02 55\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=100; -255' | bc", "- 02 55\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=4; obase=100; 1/3' | bc", ".33 33\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=1000; 123456789' | bc", " 123 456 789\n", 0, true)]
    [InlineData("printf '%b\\n' 'A' | bc", "10\n", 0, true)]
    [InlineData("printf '%b\\n' 'ibase=16; 1F' | bc", "31\n", 0, true)]
    [InlineData("printf '%b\\n' 'ibase=10; AA' | bc", "99\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=4;obase=16;10/3' | bc", "3.5553\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase=16; 255.5' | bc", "FF.8\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; s(1)' | bc -l", ".84147098480789650665\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; c(1)' | bc -l", ".54030230586813971740\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; a(1)' | bc -l", ".78539816339744830961\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; 4*a(1)' | bc -l", "3.14159265358979323844\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; l(2)' | bc -l", ".69314718055994530941\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; e(1)' | bc -l", "2.71828182845904523536\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=50; 4*a(1)' | bc -l", "3.14159265358979323846264338327950288419716939937508\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; j(0,1)' | bc -l", ".76519768655796655144\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; j(1,2)' | bc -l", ".57672480775687338720\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=30; s(0.5)' | bc -l", ".479425538604203000273287935215\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; e(2)' | bc -l", "7.38905609893065022723\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; l(10)' | bc -l", "2.30258509299404568401\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; c(0)' | bc -l", "1.00000000000000000000\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; e(0)' | bc -l", "1.00000000000000000000\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=20; s(0)' | bc -l", "0\n", 0, true)]
    [InlineData("printf '%b\\n' 'define f(x){return x*x}\\nf(5)\\nf(3)+1' | bc", "25\n10\n", 0, true)]
    [InlineData("printf '%b\\n' 'define fac(n){if(n<=1)return 1;return n*fac(n-1)}\\nfac(10)' | bc", "3628800\n", 0, true)]
    [InlineData("printf '%b\\n' 'define g(n){auto i,s;for(i=1;i<=n;i++)s+=i;return s}\\ng(100)' | bc", "5050\n", 0, true)]
    [InlineData("printf '%b\\n' 'x=7;x+=3;x' | bc", "10\n", 0, true)]
    [InlineData("printf '%b\\n' 'y=5;y++;y' | bc", "5\n6\n", 0, true)]
    [InlineData("printf '%b\\n' 'z=3;z--;z' | bc", "3\n2\n", 0, true)]
    [InlineData("printf '%b\\n' '7\\n.+1\\nlast*2' | bc", "7\n8\n16\n", 0, true)]
    [InlineData("printf '%b\\n' 'a[0]=10;a[1]=20;a[0]+a[1]' | bc", "30\n", 0, true)]
    [InlineData("printf '%b\\n' 'x=2;x^=10;x' | bc", "1024\n", 0, true)]
    [InlineData("printf '%b\\n' 'x=10;x%=3;x' | bc", "1\n", 0, true)]
    [InlineData("printf '%b\\n' 'if(3>2)print \"yes\\n\"' | bc", "yes\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale=5;x=sqrt(2);x' | bc", "1.41421\n", 0, true)]
    [InlineData("printf '%b\\n' 'define h(a[]){a[0]=99;return 0}\\nx[0]=1;h(x[]);x[0]' | bc", "0\n1\n", 0, true)]
    [InlineData("printf '%b\\n' '(x=5)' | bc", "5\n", 0, true)]
    [InlineData("printf '%b\\n' '3; 4; 5' | bc", "3\n4\n5\n", 0, true)]
    [InlineData("printf '%b\\n' 'n=5;t=0;for(i=1;i<=n;i++){t+=i};t' | bc", "15\n", 0, true)]
    [InlineData("printf '%b\\n' '2 /* c */ + 3 # trailing' | bc", "5\n", 0, true)]
    [InlineData("printf '%b\\n' 'print 2+3, \" \", 4*5, \"\\n\"' | bc", "5 20\n", 0, true)]
    [InlineData("printf '%b\\n' 'print \"hi\\n\"' | bc", "hi\n", 0, true)]
    [InlineData("printf '%b\\n' '\"bare string\\n\"' | bc", "bare string\n", 0, true)]
    [InlineData("printf '%b\\n' 'i=0;while(i<3){print i,\" \";i+=1}' | bc", "0 1 2 ", 0, true)]
    [InlineData("printf '%b\\n' 'scale' | bc", "0\n", 0, true)]
    [InlineData("printf '%b\\n' 'scale' | bc -l", "20\n", 0, true)]
    [InlineData("printf '%b\\n' 'ibase' | bc", "10\n", 0, true)]
    [InlineData("printf '%b\\n' 'obase' | bc", "10\n", 0, true)]
    public Task Core(string script, string stdout, int exitCode, bool quiet) => BashCompat.Check(script, stdout, exitCode, quiet);

    // The number cap: each over-large computation stops with a message on stderr (quiet == false) and a non-zero
    // exit, and prints nothing. All three hit the 100000-digit cap immediately (no waiting).
    [Theory]
    [InlineData("printf '%b\\n' '9^9^9' | bc", 1)]
    [InlineData("printf '%b\\n' 'scale=100000;sqrt(2)' | bc", 1)]
    [InlineData("printf '%b\\n' '2^500000' | bc", 1)]
    public Task Limits(string script, int exitCode) => BashCompat.Check(script, "", exitCode, quiet: false);

    // A runaway loop is interruptible: the eval loop checks the cancellation token, so `timeout` stops `while(1){}`
    // (exit 124) within the second. The separate 5 s per-bc wall-clock cap is exercised the same way; it is not
    // asserted here because a 5 s CPU spin in the test suite would make the timing-sensitive LimitTests flaky.
    [Fact]
    public Task RunawayLoopIsInterruptible() =>
        BashCompat.Check("printf '%b\\n' 'while(1){}' | timeout 0.3 bc; echo \"rc=$?\"", "rc=124\n", 0, quiet: true);

    // bc --version prints only the version line, with no FSF/GPL copyright notice.
    [Fact]
    public Task Version() => BashCompat.Check("bc --version", "bc 1.07.1\n", 0, quiet: true);
}
