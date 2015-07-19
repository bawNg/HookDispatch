using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Oxide.Plugins
{
    public abstract class Plugin
    {
        public virtual bool DirectCallHook(string name, out object ret, object[] args)
        {
            ret = null;
            return false;
        }
    }
}