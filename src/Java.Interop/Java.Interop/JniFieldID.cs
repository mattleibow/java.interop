using System;
using System.Runtime.InteropServices;

namespace Java.Interop
{
	public abstract class JniFieldID : SafeHandle
	{
		internal JniFieldID ()
			: base (IntPtr.Zero, true)
		{
			JniEnvironment.Current.JavaVM.Track (this, this);
		}

		protected override bool ReleaseHandle ()
		{
			JniEnvironment.Current.JavaVM.UnTrack (this);
			return true;
		}

		public override bool IsInvalid {
			get {
				return handle == IntPtr.Zero;
			}
		}

		public override string ToString ()
		{
			return string.Format ("{0}(0x{1})", GetType ().FullName, handle.ToString ("x"));
		}
	}
}

