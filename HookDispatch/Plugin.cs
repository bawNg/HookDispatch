using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

namespace Oxide.Plugins
{
    public abstract class Plugin
    {
        Dictionary<string, MethodInfo> hookMethods = new Dictionary<string, MethodInfo>();

        public virtual bool CallHook(string name, out object ret, object[] args)
        {
            MethodInfo method;
            if (!hookMethods.TryGetValue(name, out method))
            {
                method = GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
                hookMethods[name] = method;
            }
            ret = method.Invoke(this, args);
            return false;
        }

        public virtual bool DirectCallHook(string name, out object ret, object[] args)
        {
            ret = null;
            return false;
        }
    }
}