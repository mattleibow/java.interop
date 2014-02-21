﻿using System;

namespace Java.Interop
{
	public class JavaObjectArray<T> : JavaArray<T>
	{
		public JavaObjectArray (JniReferenceSafeHandle handle, JniHandleOwnership transfer)
			: base (handle, transfer)
		{
		}

		static JniLocalReference _NewArray (int length)
		{
			var info = JniEnvironment.Current.JavaVM.GetJniTypeInfoForType (typeof (T));
			if (info.JniTypeName == null)
				info.JniTypeName = "java/lang/Object";
			using (var t = new JniType (info.ToString ())) {
				return JniEnvironment.Arrays.NewObjectArray (length, t.SafeHandle, JniReferenceSafeHandle.Null);
			}
		}

		public JavaObjectArray (int length)
			: this (_NewArray (length), JniHandleOwnership.Transfer)
		{
		}

		public override T this [int index] {
			get {
				if (index < 0 || index >= Length)
					throw new ArgumentOutOfRangeException ("index", "index < 0 || index >= Length");
				var lref = JniEnvironment.Arrays.GetObjectArrayElement (SafeHandle, index);
				return JniMarshal.GetValue<T> (lref, JniHandleOwnership.Transfer);
			}
			set {
				if (index < 0 || index >= Length)
					throw new ArgumentOutOfRangeException ("index", "index < 0 || index >= Length");
				using (var h = JniMarshal.CreateLocalRef (value))
					JniEnvironment.Arrays.SetObjectArrayElement (SafeHandle, index, h);
			}
		}
	}
}

