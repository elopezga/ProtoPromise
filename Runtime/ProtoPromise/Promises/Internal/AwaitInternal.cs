﻿#if PROTO_PROMISE_DEBUG_ENABLE || (!PROTO_PROMISE_DEBUG_DISABLE && DEBUG)
#define PROMISE_DEBUG
#else
#undef PROMISE_DEBUG
#endif

#if CSHARP_7_OR_LATER // await not available in old runtime.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Proto.Promises.Async.CompilerServices;
using Proto.Utils;

namespace Proto.Promises
{
    partial class Internal
    {
        partial class PromiseRef
        {
            internal void AddAwaiter(ITreeHandleable awaiter, int promiseId)
            {
                // TODO: thread synchronization
                ThrowIfInPool(this);
                MarkAwaited(promiseId);
                _suppressRejection = true;
                AddWaiter(awaiter);
            }

            internal T MarkAwaitedAndGetResultAndMaybeDispose<T>(int promiseId)
            {
                ThrowIfInPool(this);
                MarkAwaited(promiseId);
                T result = ((ResolveContainer<T>) _valueOrPrevious).value;
                MaybeDispose();
                return result;
            }
        }

#if !PROTO_PROMISE_DEVELOPER_MODE
        [DebuggerNonUserCode]
#endif
        internal sealed partial class AwaiterRef : ITreeHandleable
        {
            private struct Creator : ICreator<AwaiterRef>
            {
                [MethodImpl((MethodImplOptions) 256)]
                public AwaiterRef Create()
                {
                    return new AwaiterRef();
                }
            }

            ITreeHandleable ILinked<ITreeHandleable>.Next { get; set; }

            private Action _continuation;
            private IValueContainer _valueContainer;
            private Promise.State _state;
            private int _id;

            internal int Id
            {
                [MethodImpl((MethodImplOptions) 256)]
                get { return _id; }
            }

            private AwaiterRef() { }

            [MethodImpl((MethodImplOptions) 256)]
            internal static AwaiterRef GetOrCreate()
            {
                var awaiter = ObjectPool<ITreeHandleable>.GetOrCreate<AwaiterRef, Creator>(new Creator());
                awaiter._state = Promise.State.Pending;
                return awaiter;
            }

            private void Dispose()
            {
                var temp = _valueContainer;
                _valueContainer = null;
                temp.Release();
                ObjectPool<ITreeHandleable>.MaybeRepool(this);
            }

            [MethodImpl((MethodImplOptions) 256)]
            private void IncrementId(int promiseId)
            {
                unchecked
                {
                    if (Interlocked.CompareExchange(ref _id, promiseId + 1, promiseId) != promiseId)
                    {
                        ThrowFromIdMismatch();
                    }
                }
            }

            [MethodImpl((MethodImplOptions) 256)]
            internal bool GetCompleted(int awaiterId)
            {
                ValidateId(awaiterId);
                ThrowIfInPool(this);
                return _state != Promise.State.Pending;
            }

            [MethodImpl((MethodImplOptions) 256)]
            internal void OnCompleted(Action continuation, int awaiterId)
            {
                ValidateId(awaiterId);
                ThrowIfInPool(this);
                _continuation = continuation;
            }

            [MethodImpl((MethodImplOptions) 256)]
            internal void GetResult(int awaiterId)
            {
                ThrowIfInPool(this);
#if PROMISE_DEBUG
                if (_state == Promise.State.Pending)
                {
                    throw new InvalidOperationException("PromiseAwaiter.GetResult() is only valid when the promise is completed. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", GetFormattedStacktrace(2));
                }
#endif
                IncrementId(awaiterId);
                if (_state == Promise.State.Resolved)
                {
                    Dispose();
                    return;
                }
                // Throw unhandled exception or canceled exception.
                Exception exception = ((IThrowable) _valueContainer).GetException();
                Dispose();
                throw exception;
            }

            [MethodImpl((MethodImplOptions) 256)]
            internal T GetResult<T>(int awaiterId)
            {
                ThrowIfInPool(this);
#if PROMISE_DEBUG
                if (_state == Promise.State.Pending)
                {
                    throw new InvalidOperationException("PromiseAwaiter<T>.GetResult() is only valid when the promise is completed. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", GetFormattedStacktrace(2));
                }
#endif
                IncrementId(awaiterId);
                if (_state == Promise.State.Resolved)
                {
                    T result = ((ResolveContainer<T>) _valueContainer).value;
                    Dispose();
                    return result;
                }
                // Throw unhandled exception or canceled exception.
                Exception exception = ((IThrowable) _valueContainer).GetException();
                Dispose();
                throw exception;
            }

            void ITreeHandleable.Handle()
            {
                ThrowIfInPool(this);
                var callback = _continuation;
                if (callback != null)
                {
                    _continuation = null;
                    callback.Invoke();
                }
            }

            void ITreeHandleable.MakeReady(PromiseRef owner, IValueContainer valueContainer, ref ValueLinkedQueue<ITreeHandleable> handleQueue)
            {
                ThrowIfInPool(this);
                valueContainer.Retain();
                _valueContainer = valueContainer;
                _state = owner.State;
                handleQueue.Push(this);
            }

            void ITreeHandleable.MakeReadyFromSettled(PromiseRef owner, IValueContainer valueContainer)
            {
                ThrowIfInPool(this);
                valueContainer.Retain();
                _valueContainer = valueContainer;
                _state = owner.State;
            }

            private void ThrowFromIdMismatch()
            {
                throw new InvalidOperationException("PromiseAwaiter is not valid. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", GetFormattedStacktrace(3));

            }

            partial void ValidateId(int awaiterId);
#if PROMISE_DEBUG
            partial void ValidateId(int awaiterId)
            {
                if (Interlocked.CompareExchange(ref _id, 0, 0) != awaiterId)
                {
                    ThrowFromIdMismatch();
                }
            }
#endif
        }
    }

    namespace Async.CompilerServices
    {
        /// <summary>
        /// Used to support the await keyword.
        /// </summary>
#if !PROTO_PROMISE_DEVELOPER_MODE
        [DebuggerNonUserCode]
#endif
        public
#if CSHARP_7_3_OR_NEWER
            readonly
#endif
            partial struct PromiseAwaiter : ICriticalNotifyCompletion
        {
            private readonly Internal.AwaiterRef _ref;
            private readonly int _id;

            /// <summary>
            /// Internal use.
            /// </summary>
            [MethodImpl((MethodImplOptions) 256)]
            internal PromiseAwaiter(Promise promise)
            {
                if (promise._ref == null)
                {
                    _ref = null;
                    _id = Internal.ValidPromiseIdFromApi;
                }
                else if (promise._ref.State == Promise.State.Resolved) // No need to allocate a new object if the promise is resolved.
                {
                    _ref = null;
                    _id = Internal.ValidPromiseIdFromApi;
                    promise._ref.MarkAwaitedAndMaybeDispose(promise._id, true);
                }
                else
                {
                    _ref = Internal.AwaiterRef.GetOrCreate();
                    _id = _ref.Id;
                    promise._ref.AddAwaiter(_ref, promise._id);
                }
            }

            public bool IsCompleted
            {
                [MethodImpl((MethodImplOptions) 256)]
                get
                {
                    ValidateOperation(1);

                    return _ref == null || _ref.GetCompleted(_id);
                }
            }

            [MethodImpl((MethodImplOptions) 256)]
            public void GetResult()
            {
                ValidateOperation(1);

                if (_ref != null)
                {
                    _ref.GetResult(_id);
                }
            }

            [MethodImpl((MethodImplOptions) 256)]
            public void OnCompleted(Action continuation)
            {
                ValidateOperation(1);

#if PROMISE_DEBUG
                if (_ref == null)
                {
                    throw new InvalidOperationException("PromiseAwaiter.OnCompleted is not a valid operation at this time. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", Internal.GetFormattedStacktrace(1));
                }
#endif
                _ref.OnCompleted(continuation, _id);
            }

            [MethodImpl((MethodImplOptions) 256)]
            public void UnsafeOnCompleted(Action continuation)
            {
                OnCompleted(continuation);
            }

            partial void ValidateOperation(int skipFrames);
#if PROMISE_DEBUG
            partial void ValidateOperation(int skipFrames)
            {
                bool isValid = _id == Internal.ValidPromiseIdFromApi | (_ref != null && _id == _ref.Id);
                if (!isValid)
                {
                    throw new InvalidOperationException("PromiseAwaiter is not valid. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", Internal.GetFormattedStacktrace(skipFrames + 1));
                }
            }
#endif
        }

        /// <summary>
        /// Used to support the await keyword.
        /// </summary>
#if !PROTO_PROMISE_DEVELOPER_MODE
        [DebuggerNonUserCode]
#endif
        public
#if CSHARP_7_3_OR_NEWER
            readonly
#endif
            partial struct PromiseAwaiter<T> : ICriticalNotifyCompletion
        {
            private readonly Internal.AwaiterRef _ref;
            private readonly int _id;
            private readonly T _result;

            /// <summary>
            /// Internal use.
            /// </summary>
            [MethodImpl((MethodImplOptions) 256)]
            internal PromiseAwaiter(Promise<T> promise)
            {
                if (promise._ref == null)
                {
                    _ref = null;
                    _id = Internal.ValidPromiseIdFromApi;
                    _result = promise._result;
                }
                else if (promise._ref.State == Promise.State.Resolved) // No need to allocate a new object if the promise is resolved.
                {
                    _ref = null;
                    _id = Internal.ValidPromiseIdFromApi;
                    _result = promise._ref.MarkAwaitedAndGetResultAndMaybeDispose<T>(promise._id);
                }
                else
                {
                    _ref = Internal.AwaiterRef.GetOrCreate();
                    _id = _ref.Id;
                    promise._ref.AddAwaiter(_ref, promise._id);
                    _result = default(T);
                }
            }

            public bool IsCompleted
            {
                [MethodImpl((MethodImplOptions) 256)]
                get
                {
                    ValidateOperation(1);

                    return _ref == null || _ref.GetCompleted(_id);
                }
            }

            [MethodImpl((MethodImplOptions) 256)]
            public T GetResult()
            {
                ValidateOperation(1);

                if (_ref != null)
                {
                    return _ref.GetResult<T>(_id);
                }
                return _result;
            }

            [MethodImpl((MethodImplOptions) 256)]
            public void OnCompleted(Action continuation)
            {
                ValidateOperation(1);

#if PROMISE_DEBUG
                if (_ref == null)
                {
                    throw new InvalidOperationException("PromiseAwaiter.OnCompleted is not a valid operation at this time. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", Internal.GetFormattedStacktrace(1));
                }
#endif
                _ref.OnCompleted(continuation, _id);
            }

            [MethodImpl((MethodImplOptions) 256)]
            public void UnsafeOnCompleted(Action continuation)
            {
                OnCompleted(continuation);
            }

            partial void ValidateOperation(int skipFrames);
#if PROMISE_DEBUG
            partial void ValidateOperation(int skipFrames)
            {
                bool isValid = _id == Internal.ValidPromiseIdFromApi | (_ref != null && _id == _ref.Id);
                if (!isValid)
                {
                    throw new InvalidOperationException("PromiseAwaiter is not valid. Use the 'await' keyword on a Promise instead of using PromiseAwaiter.", Internal.GetFormattedStacktrace(skipFrames + 1));
                }
            }
#endif
        }
    }

    partial struct Promise
    {
        // TODO: ConfigureAwait taking CancelationToken, ExecutionOptions, and/or progress normalization.

        /// <summary>
        /// Used to support the await keyword.
        /// </summary>
        [MethodImpl((MethodImplOptions) 256)]
        public PromiseAwaiter GetAwaiter()
        {
            ValidateOperation(1);

            return new PromiseAwaiter(this);
        }
    }

    partial struct Promise<T>
    {
        /// <summary>
        /// Used to support the await keyword.
        /// </summary>
        [MethodImpl((MethodImplOptions) 256)]
        public PromiseAwaiter<T> GetAwaiter()
        {
            ValidateOperation(1);

            return new PromiseAwaiter<T>(this);
        }
    }
}
#endif // C#7