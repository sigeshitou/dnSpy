﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Modules;
using dnSpy.Debugger.Impl;

namespace dnSpy.Debugger.Breakpoints.Modules {
	interface IDbgModuleBreakpointsServiceListener {
		void Initialize(DbgModuleBreakpointsService dbgModuleBreakpointsService);
	}

	[Export(typeof(DbgModuleBreakpointsService))]
	sealed class DbgModuleBreakpointsServiceImpl : DbgModuleBreakpointsService {
		readonly object lockObj;
		readonly HashSet<DbgModuleBreakpointImpl> breakpoints;
		readonly DbgDispatcher dbgDispatcher;

		[ImportingConstructor]
		DbgModuleBreakpointsServiceImpl(DbgDispatcher dbgDispatcher, [ImportMany] IEnumerable<Lazy<IDbgModuleBreakpointsServiceListener>> dbgModuleBreakpointsServiceListener) {
			lockObj = new object();
			breakpoints = new HashSet<DbgModuleBreakpointImpl>();
			this.dbgDispatcher = dbgDispatcher;

			foreach (var lz in dbgModuleBreakpointsServiceListener)
				lz.Value.Initialize(this);
		}

		void Dbg(Action action) => dbgDispatcher.Dbg(action);

		public override void Modify(DbgModuleBreakpointAndSettings[] settings) {
			if (settings == null)
				throw new ArgumentNullException(nameof(settings));
			Dbg(() => ModifyCore(settings));
		}

		void ModifyCore(DbgModuleBreakpointAndSettings[] settings) {
			dbgDispatcher.VerifyAccess();
			var bps = new List<DbgModuleBreakpoint>(settings.Length);
			lock (lockObj) {
				foreach (var info in settings) {
					var bpImpl = info.Breakpoint as DbgModuleBreakpointImpl;
					Debug.Assert(bpImpl != null);
					if (bpImpl == null)
						continue;
					Debug.Assert(breakpoints.Contains(bpImpl));
					if (!breakpoints.Contains(bpImpl))
						continue;
					bps.Add(bpImpl);
					bpImpl.WriteSettings(info.Settings);
				}
			}
			if (bps.Count > 0)
				BreakpointsModified?.Invoke(this, new DbgBreakpointsModifiedEventArgs(new ReadOnlyCollection<DbgModuleBreakpoint>(bps)));
		}

		public override event EventHandler<DbgBreakpointsModifiedEventArgs> BreakpointsModified;

		public override event EventHandler<DbgCollectionChangedEventArgs<DbgModuleBreakpoint>> BreakpointsChanged;
		public override DbgModuleBreakpoint[] Breakpoints {
			get {
				lock (lockObj)
					return breakpoints.ToArray();
			}
		}

		public override DbgModuleBreakpoint[] Add(DbgModuleBreakpointSettings[] settings) {
			if (settings == null)
				throw new ArgumentNullException(nameof(settings));
			// Return a copy since the caller could modify the array
			var bps = new DbgModuleBreakpoint[settings.Length];
			var bpImpls = new DbgModuleBreakpointImpl[settings.Length];
			for (int i = 0; i < bps.Length; i++) {
				var bp = new DbgModuleBreakpointImpl(this, settings[i]);
				bps[i] = bp;
				bpImpls[i] = bp;
			}
			Dbg(() => AddCore(bpImpls));
			return bps;
		}

		void AddCore(DbgModuleBreakpointImpl[] breakpoints) {
			dbgDispatcher.VerifyAccess();
			var added = new List<DbgModuleBreakpoint>(breakpoints.Length);
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					Debug.Assert(!this.breakpoints.Contains(bp));
					if (this.breakpoints.Contains(bp))
						continue;
					added.Add(bp);
					this.breakpoints.Add(bp);
				}
			}
			if (added.Count > 0)
				BreakpointsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModuleBreakpoint>(added, added: true));
		}

		public override void Remove(DbgModuleBreakpoint[] breakpoints) {
			if (breakpoints == null)
				throw new ArgumentNullException(nameof(breakpoints));
			Dbg(() => RemoveCore(breakpoints));
		}

		void RemoveCore(DbgModuleBreakpoint[] breakpoints) {
			dbgDispatcher.VerifyAccess();
			var removed = new List<DbgModuleBreakpoint>(breakpoints.Length);
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					var bpImpl = bp as DbgModuleBreakpointImpl;
					Debug.Assert(bpImpl != null);
					if (bpImpl == null)
						continue;
					Debug.Assert(this.breakpoints.Contains(bpImpl));
					if (!this.breakpoints.Contains(bpImpl))
						continue;
					removed.Add(bpImpl);
					this.breakpoints.Remove(bpImpl);
				}
			}
			if (removed.Count > 0) {
				BreakpointsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModuleBreakpoint>(removed, added: false));
				foreach (var bp in removed)
					bp.Close(dbgDispatcher.DispatcherThread);
			}
		}

		public override void Clear() => Dbg(() => ClearCore());
		void ClearCore() {
			dbgDispatcher.VerifyAccess();
			DbgModuleBreakpoint[] removed;
			lock (lockObj) {
				removed = breakpoints.ToArray();
				breakpoints.Clear();
			}
			if (removed.Length > 0) {
				BreakpointsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgModuleBreakpoint>(removed, added: false));
				foreach (var bp in removed)
					bp.Close(dbgDispatcher.DispatcherThread);
			}
		}

		public override DbgModuleBreakpoint[] Find(DbgBreakpointModule module) {
			List<DbgModuleBreakpoint> foundBps = null;
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					if (bp.IsMatch(module)) {
						if (foundBps == null)
							foundBps = new List<DbgModuleBreakpoint>();
						foundBps.Add(bp);
					}
				}
			}
			return foundBps == null ? Array.Empty<DbgModuleBreakpoint>() : foundBps.ToArray();
		}

		public override bool IsMatch(DbgBreakpointModule module) {
			lock (lockObj) {
				foreach (var bp in breakpoints) {
					if (bp.IsMatch(module))
						return true;
				}
			}
			return false;
		}
	}
}
