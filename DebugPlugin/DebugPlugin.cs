using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Oxide.Plugins
{
    public class DebugPlugin : Plugin
    {
        object OnMy(string text)
        {
            Console.WriteLine("HookMethod called: OnMy - " + text ?? "null");
            return null;
        }

        /*object OnMy2(string text)
        {
            Console.WriteLine("HookMethod called: OnMy2 - " + text ?? "null");
            return null;
        }*/

        object OnYour(string text)
        {
            Console.WriteLine("HookMethod called: OnYour - " + text ?? "null");
            return null;
        }
    }
}
