﻿using System.Collections.Generic;

namespace System.Threading
{
    /// <summary>
    /// Signals to a <see cref="System.Threading.CancellationToken"/> that it should be canceled.
    /// </summary>
    /// <remarks> 
    /// <para>
    /// <see cref="T:System.Threading.CancellationTokenSource"/> is used to instantiate a <see 
    /// cref="T:System.Threading.CancellationToken"/> 
    /// (via the source's <see cref="System.Threading.CancellationTokenSource.Token">Token</see> property)
    /// that can be handed to operations that wish to be notified of cancellation or that can be used to 
    /// register asynchronous operations for cancellation. That token may have cancellation requested by
    /// calling to the source's <see cref="System.Threading.CancellationTokenSource.Cancel()">Cancel</see>
    /// method.
    /// </para> 
    /// <para>
    /// All members of this class, except <see cref="Dispose">Dispose</see>, are thread-safe and may be used 
    /// concurrently from multiple threads. 
    /// </para>
    /// </remarks> 
    public sealed class CancellationTokenSource : IDisposable
    { 
        //static sources that can be used as the backing source for 'fixed' CancellationTokens that never change state.
        private static readonly CancellationTokenSource _staticSource_Set = new CancellationTokenSource(true); 
        private static readonly CancellationTokenSource _staticSource_NotCancelable = new CancellationTokenSource(false); 

        //Note: the callback lists array is only created on first registration. 
        //      the actual callback lists are only created on demand.
        //      Storing a registered callback costs around >60bytes, hence some overhead for the lists array is OK
        // At most 24 lists seems reasonable, and caps the cost of the listsArray to 96bytes(32-bit,24-way) or 192bytes(64-bit,24-way).
        private static readonly int s_nLists = 1; 

        private SparselyPopulatedArray<CancellationCallbackInfo> m_registeredCallbacksList;
 
        // legal values for m_state
        private const int CANNOT_BE_CANCELED = 0;
        private const int NOT_CANCELED = 1;
        private const int NOTIFYING = 2; 
        private const int NOTIFYINGCOMPLETE = 3;
 
        //m_state uses the pattern "volatile int32 reads, with cmpxch writes" which is safe for updates and cannot suffer torn reads. 
        private int m_state;
 
        private bool m_disposed;
 
        private List<CancellationTokenRegistration> m_linkingRegistrations; //lazily initialized if required.

        private static readonly Action<object> s_LinkedTokenCancelDelegate = new Action<object>(LinkedTokenCancelDelegate);
 
        // we track the running callback to assist ctr.Dispose() to wait for the target callback to complete.
        private CancellationCallbackInfo m_executingCallback; 
 
        private static void LinkedTokenCancelDelegate(object source)
        { 
            var cts = source as CancellationTokenSource;
            cts.Cancel();
        } 

        // ---------------------- 
        // ** public properties 

        /// <summary> 
        /// Gets whether cancellation has been requested for this <see
        /// cref="System.Threading.CancellationTokenSource">CancellationTokenSource</see>.
        /// </summary>
        /// <value>Whether cancellation has been requested for this <see 
        /// cref="System.Threading.CancellationTokenSource">CancellationTokenSource</see>.</value>
        /// <remarks> 
        /// <para> 
        /// This property indicates whether cancellation has been requested for this token source, such as
        /// due to a call to its 
        /// <see cref="System.Threading.CancellationTokenSource.Cancel()">Cancel</see> method.
        /// </para>
        /// <para>
        /// If this property returns true, it only guarantees that cancellation has been requested. It does not 
        /// guarantee that every handler registered with the corresponding token has finished executing, nor
        /// that cancellation requests have finished propagating to all registered handlers. Additional 
        /// synchronization may be required, particularly in situations where related objects are being 
        /// canceled concurrently.
        /// </para> 
        /// </remarks>
        public bool IsCancellationRequested
        {
            get { return m_state >= NOTIFYING; } 
        }
 
        /// <summary> 
        /// A simple helper to determine whether cancellation has finished.
        /// </summary> 
        internal bool IsCancellationCompleted
        {
            get { return m_state == NOTIFYINGCOMPLETE; }
        } 

        /// <summary> 
        /// A simple helper to determine whether disposal has occured. 
        /// </summary>
        internal bool IsDisposed 
        {
            get { return m_disposed; }
        }
 
        /// <summary>
        /// Gets the <see cref="System.Threading.CancellationToken">CancellationToken</see> 
        /// associated with this <see cref="CancellationTokenSource"/>. 
        /// </summary>
        /// <value>The <see cref="System.Threading.CancellationToken">CancellationToken</see> 
        /// associated with this <see cref="CancellationTokenSource"/>.</value>
        /// <exception cref="T:System.ObjectDisposedException">The token source has been
        /// disposed.</exception>
        public CancellationToken Token 
        {
            get 
            { 
                ThrowIfDisposed();
                return new CancellationToken(this); 
            }
        }

        // ---------------------- 
        // ** internal and private properties.
 
        /// <summary> 
        ///
        /// </summary> 
        internal bool CanBeCanceled
        {
            get { return m_state != CANNOT_BE_CANCELED; }
        } 

        /// <summary>
        /// The currently executing callback
        /// </summary> 
        internal CancellationCallbackInfo ExecutingCallback
        { 
            get { return m_executingCallback; } 
        }
 
        /// <summary>
        /// Initializes the <see cref="T:System.Threading.CancellationTokenSource"/>.
        /// </summary> 
        public CancellationTokenSource()
        { 
            m_state = NOT_CANCELED; 
        }
 
        // ** Private constructors for static sources.
        // set=false ==> cannot be canceled.
        // set=true  ==> is canceled.
        private CancellationTokenSource(bool set) 
        {
            m_state = set ? NOTIFYINGCOMPLETE : CANNOT_BE_CANCELED; 
        } 

        /// <summary>
        /// Communicates a request for cancellation.
        /// </summary> 
        /// <remarks>
        /// <para> 
        /// The associated <see cref="T:System.Threading.CancellationToken" /> will be 
        /// notified of the cancellation and will transition to a state where
        /// <see cref="System.Threading.CancellationToken.IsCancellationRequested">IsCancellationRequested</see> returns true. 
        /// Any callbacks or cancelable operations
        /// registered with the <see cref="T:System.Threading.CancellationToken"/>  will be executed.
        /// </para>
        /// <para> 
        /// Cancelable operations and callbacks registered with the token should not throw exceptions.
        /// However, this overload of Cancel will aggregate any exceptions thrown into a <see cref="System.AggregateException"/>, 
        /// such that one callback throwing an exception will not prevent other registered callbacks from being executed. 
        /// </para>
        /// <para> 
        /// The <see cref="T:System.Threading.ExecutionContext"/> that was captured when each callback was registered
        /// will be reestablished when the callback is invoked.
        /// </para>
        /// </remarks> 
        /// <exception cref="T:System.AggregateException">An aggregate exception containing all the exceptions thrown
        /// by the registered callbacks on the associated <see cref="T:System.Threading.CancellationToken"/>.</exception> 
        /// <exception cref="T:System.ObjectDisposedException">This <see 
        /// cref="T:System.Threading.CancellationTokenSource"/> has been disposed.</exception>
        public void Cancel() 
        {
            Cancel(false);
        }
 
        /// <summary>
        /// Communicates a request for cancellation. 
        /// </summary> 
        /// <remarks>
        /// <para> 
        /// The associated <see cref="T:System.Threading.CancellationToken" /> will be
        /// notified of the cancellation and will transition to a state where
        /// <see cref="System.Threading.CancellationToken.IsCancellationRequested">IsCancellationRequested</see> returns true.
        /// Any callbacks or cancelable operations 
        /// registered with the <see cref="T:System.Threading.CancellationToken"/>  will be executed.
        /// </para> 
        /// <para> 
        /// Cancelable operations and callbacks registered with the token should not throw exceptions.
        /// If <paramref name="throwOnFirstException"/> is true, an exception will immediately propagate out of the 
        /// call to Cancel, preventing the remaining callbacks and cancelable operations from being processed.
        /// If <paramref name="throwOnFirstException"/> is false, this overload will aggregate any
        /// exceptions thrown into a <see cref="System.AggregateException"/>,
        /// such that one callback throwing an exception will not prevent other registered callbacks from being executed. 
        /// </para>
        /// <para> 
        /// The <see cref="T:System.Threading.ExecutionContext"/> that was captured when each callback was registered 
        /// will be reestablished when the callback is invoked.
        /// </para> 
        /// </remarks>
        /// <param name="throwOnFirstException">Specifies whether exceptions should immediately propagate.</param>
        /// <exception cref="T:System.AggregateException">An aggregate exception containing all the exceptions thrown
        /// by the registered callbacks on the associated <see cref="T:System.Threading.CancellationToken"/>.</exception> 
        /// <exception cref="T:System.ObjectDisposedException">This <see
        /// cref="T:System.Threading.CancellationTokenSource"/> has been disposed.</exception> 
        public void Cancel(bool throwOnFirstException) 
        {
            ThrowIfDisposed(); 
            NotifyCancellation(throwOnFirstException);
        }

        /// <summary> 
        /// Releases the resources used by this <see cref="T:System.Threading.CancellationTokenSource" />.
        /// </summary> 
        /// <remarks> 
        /// This method is not thread-safe for any other concurrent calls.
        /// </remarks> 
        public void Dispose()
        {
            if (m_disposed)
                return; 

            bool isLinked = m_linkingRegistrations != null; 
            if (isLinked) 
            {
                foreach (CancellationTokenRegistration registration in m_linkingRegistrations) 
                {
                    registration.Dispose();
                }
                m_linkingRegistrations = null; //free for GC 
            }
 
            // registered callbacks are now either complete or will never run, due to guarantees made by ctr.Dispose() 
            // so we can now perform main disposal work without risk of linking callbacks trying to use this CTS.
 
            m_registeredCallbacksList = null; // free for GC.

            m_disposed = true; 
        }

        // -- Internal methods.
 
        /// <summary>
        /// Throws an exception if the source has been disposed. 
        /// </summary> 
        internal void ThrowIfDisposed()
        { 
            if (m_disposed)
                throw new ObjectDisposedException("CancellationTokenSource_Disposed");
        }
 
        /// <summary>
        /// InternalGetStaticSource() 
        /// </summary> 
        /// <param name="set">Whether the source should be set.</param>
        /// <returns>A static source to be shared among multiple tokens.</returns> 
        internal static CancellationTokenSource InternalGetStaticSource(bool set)
        {
            return set ? _staticSource_Set : _staticSource_NotCancelable;
        } 

        /// <summary> 
        /// Registers a callback object. If cancellation has already occurred, the 
        /// callback will have been run by the time this method returns.
        /// </summary> 
        internal CancellationTokenRegistration InternalRegister(Action<object> callback, object stateForCallback)
        {
            ThrowIfDisposed(); 

            // if not canceled, register the event handlers 
            // if canceled already, run the callback synchronously
            // Apart from the semantics of late-enlistment, this also ensures that during ExecuteCallbackHandlers() there
            // will be no mutation of the _registeredCallbacks list
 
            if (!IsCancellationRequested)
            { 
                var callbackInfo = new CancellationCallbackInfo(callback, stateForCallback, this); 

                //allocate the callback list array
                if (m_registeredCallbacksList == null)
                { 
                    m_registeredCallbacksList = new SparselyPopulatedArray<CancellationCallbackInfo>(4);
                }
 
                // Now add the registration to the list. 
                var callbacks = m_registeredCallbacksList;
                var addInfo = callbacks.Add(callbackInfo); 
                var registration = new CancellationTokenRegistration(this, callbackInfo, addInfo);

                if (!IsCancellationRequested)
                    return registration; 

                //If a cancellation has since come in, we will try to undo the registration and run the callback directly here. 
                registration.TryDeregister(); 
            }
 
            // If cancellation already occurred, we run the callback on this thread and return an empty registration.
            callback(stateForCallback);
            return new CancellationTokenRegistration();
        } 

        /// <summary> 
        /// 
        /// </summary>
        private void NotifyCancellation(bool throwOnFirstException) 
        {
            // fast-path test to check if Notify has been called previously
            if (IsCancellationRequested)
                return; 

            // If we're the first to signal cancellation, do the main extra work. 
            if (Interlocked.CompareExchange(ref m_state, NOTIFYING, NOT_CANCELED) == NOT_CANCELED) 
            {
                //If the kernel event is null at this point, it will be set during lazy construction.
//                if (m_kernelEvent != null) 
//                    m_kernelEvent.Set(); // update the MRE value.
 
                // - late enlisters to the Canceled event will have their callbacks called immediately in the Register() methods. 
                // - Callbacks are not called inside a lock.
                // - After transition, no more delegates will be added to the 
                // - list of handlers, and hence it can be consumed and cleared at leisure by ExecuteCallbackHandlers.
                ExecuteCallbackHandlers(throwOnFirstException);
            } 
        }
 
        /// <summary> 
        /// Invoke the Canceled event.
        /// </summary> 
        /// <remarks>
        /// The handlers are invoked synchronously in LIFO order.
        /// </remarks>
        private void ExecuteCallbackHandlers(bool throwOnFirstException) 
        {
            // Design decision: call the delegates in LIFO order so that callbacks fire 'deepest first'. 
            // This is intended to help with nesting scenarios so that child enlisters cancel before their parents.
            List<Exception> exceptionList = null;
 
            // If there are no callbacks to run, we can safely exit.  Any ----s to lazy initialize it
            // will see IsCancellationRequested and will then run the callback themselves. 
            if (m_registeredCallbacksList == null) 
            {
                Interlocked.Exchange(ref m_state, NOTIFYINGCOMPLETE); 
                return;
            }

            try 
            {
                var list = m_registeredCallbacksList;
                if (list != null) 
                {
                    var currArrayFragment = list.Tail;

                    while (currArrayFragment != null) 
                    {
                        for (int i = currArrayFragment.Length - 1; i >= 0; i--) 
                        { 
                            // 1a. publish the indended callback, to ensure ctr.Dipose can tell if a wait is necessary.
                            // 1b. transition to the target syncContext and continue there.. 
                            //  On the target SyncContext.
                            //   2. actually remove the callback
                            //   3. execute the callback
                            // re:#2 we do the remove on the syncCtx so that we can be sure we have control of the syncCtx before 
                            //        grabbing the callback.  This prevents a deadlock if ctr.Dispose() might run on the syncCtx too.
                            m_executingCallback = currArrayFragment[i]; 
                            if (m_executingCallback != null) 
                            {
                                //Transition to the target [....] context (if necessary), and continue our work there. 
                                var args = new CancellationCallbackCoreWorkArguments(currArrayFragment, i);

                                // marshal exceptions: either aggregate or perform an immediate rethrow
                                // We assume that syncCtx.Send() has forwarded on user exceptions when appropriate. 
                                try
                                { 
                                    CancellationCallbackCoreWork_OnSyncContext(args); 
                                } 
                                catch(Exception ex)
                                {
                                    if (throwOnFirstException)
                                        throw; 

                                    // Otherwise, log it and proceed. 
                                    if(exceptionList == null) 
                                        exceptionList = new List<Exception>();
                                    exceptionList.Add(ex); 
                                }
                            }
                        }
 
                        currArrayFragment = currArrayFragment.Prev;
                    } 
                } 
            }
            finally
            {
                m_state = NOTIFYINGCOMPLETE;
                m_executingCallback = null; 
            } 
 
            if (exceptionList != null)
            { 
                throw new AggregateException(exceptionList);
            }
        } 

        // The main callback work that executes on the target synchronization context 
        private void CancellationCallbackCoreWork_OnSyncContext(object obj) 
        {
            var args = (CancellationCallbackCoreWorkArguments)obj; 

            // now remove the intended callback..and ensure that it worked.
            // otherwise the callback has disappeared in the interim and we can immediately return.
            var callback = args.m_currArrayFragment.SafeAtomicRemove(args.m_currArrayIndex, m_executingCallback); 
            if (callback == m_executingCallback)
            { 
                callback.ExecuteCallback();
            } 
        }
 
 
        /// <summary>
        /// Creates a <see cref="T:System.Threading.CancellationTokenSource">CancellationTokenSource</see> that will be in the canceled state 
        /// when any of the source tokens are in the canceled state.
        /// </summary>
        /// <param name="token1">The first <see cref="T:System.Threading.CancellationToken">CancellationToken</see> to observe.</param>
        /// <param name="token2">The second <see cref="T:System.Threading.CancellationToken">CancellationToken</see> to observe.</param> 
        /// <returns>A <see cref="T:System.Threading.CancellationTokenSource">CancellationTokenSource</see> that is linked
        /// to the source tokens.</returns> 
        /// <exception cref="T:System.ObjectDisposedException">A <see 
        /// cref="T:System.Threading.CancellationTokenSource">CancellationTokenSource</see> associated with
        /// one of the source tokens has been disposed.</exception> 
        public static CancellationTokenSource CreateLinkedTokenSource(CancellationToken token1, CancellationToken token2)
        {
            var linkedTokenSource = new CancellationTokenSource();
            if( token1.CanBeCanceled ) 
            {
                linkedTokenSource.m_linkingRegistrations = new List<CancellationTokenRegistration>(); 
                linkedTokenSource.m_linkingRegistrations.Add(token1.InternalRegisterWithoutEC(s_LinkedTokenCancelDelegate, linkedTokenSource)); 
            }
 
            if( token2.CanBeCanceled )
            {
                if( linkedTokenSource.m_linkingRegistrations == null )
                { 
                    linkedTokenSource.m_linkingRegistrations = new List<CancellationTokenRegistration>();
                } 
                linkedTokenSource.m_linkingRegistrations.Add(token2.InternalRegisterWithoutEC(s_LinkedTokenCancelDelegate, linkedTokenSource)); 
            }
 
            return linkedTokenSource;
        }

        /// <summary> 
        /// Creates a <see cref="T:System.Threading.CancellationTokenSource">CancellationTokenSource</see> that will be in the canceled state
        /// when any of the source tokens are in the canceled state. 
        /// </summary> 
        /// <param name="tokens">The <see cref="T:System.Threading.CancellationToken">CancellationToken</see> instances to observe.</param>
        /// <returns>A <see cref="T:System.Threading.CancellationTokenSource">CancellationTokenSource</see> that is linked 
        /// to the source tokens.</returns>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="tokens"/> is null.</exception>
        /// <exception cref="T:System.ObjectDisposedException">A <see
        /// cref="T:System.Threading.CancellationTokenSource">CancellationTokenSource</see> associated with 
        /// one of the source tokens has been disposed.</exception>
        public static CancellationTokenSource CreateLinkedTokenSource(params CancellationToken[] tokens) 
        { 
            if (tokens == null)
                throw new ArgumentNullException("tokens"); 

            if (tokens.Length == 0)
                throw new ArgumentException("CancellationToken_CreateLinkedToken_TokensIsEmpty");
 
            // a defensive copy is not required as the array has value-items that have only a single IntPtr field,
            // hence each item cannot be null itself, and reads of the payloads cannot be torn. 

            var linkedTokenSource = new CancellationTokenSource(); 
            linkedTokenSource.m_linkingRegistrations = new List<CancellationTokenRegistration>();

            for (int i = 0; i < tokens.Length; i++)
            { 
                if (tokens[i].CanBeCanceled)
                { 
                    linkedTokenSource.m_linkingRegistrations.Add(tokens[i].InternalRegisterWithoutEC(s_LinkedTokenCancelDelegate, linkedTokenSource)); 
                }
            } 

            return linkedTokenSource;
        }
    }
 
    // ----------------------------------------------------------
    // -- CancellationCallbackCoreWorkArguments --
    // ---------------------------------------------------------
    // Helper struct for passing data to the target [....] context 
    internal struct CancellationCallbackCoreWorkArguments
    { 
        internal SparselyPopulatedArrayFragment<CancellationCallbackInfo> m_currArrayFragment; 
        internal int m_currArrayIndex;
 
        public CancellationCallbackCoreWorkArguments(SparselyPopulatedArrayFragment<CancellationCallbackInfo> currArrayFragment, int currArrayIndex)
        {
            m_currArrayFragment = currArrayFragment;
            m_currArrayIndex = currArrayIndex; 
        }
    } 
 
    // ---------------------------------------------------------
    // -- CancellationCallbackInfo -- 
    // ---------------------------------------------------------

    /// <summary>
    /// A helper class for collating the various bits of information required to execute 
    /// cancellation callbacks.
    /// </summary> 
    internal class CancellationCallbackInfo 
    {
        internal readonly Action<object> Callback; 
        internal readonly object StateForCallback;
        internal readonly CancellationTokenSource CancellationTokenSource; 

        internal CancellationCallbackInfo(Action<object> callback, object stateForCallback, 
            CancellationTokenSource cancellationTokenSource)
        { 
            Callback = callback;
            StateForCallback = stateForCallback;
            CancellationTokenSource = cancellationTokenSource;
        } 
 
        /// <summary>
        /// InternalExecuteCallbackSynchronously_GeneralPath 
        /// This will be called on the target synchronization context, however, we still need to restore the required execution context
        /// </summary>
        internal void ExecuteCallback() 
        {
            //otherwise run directly 
            ExecutionContextCallback(this); 
        } 

        // the worker method to actually run the callback
        // The signature is such that it can be used as a 'ContextCallback'
        private static void ExecutionContextCallback(object obj) 
        {
            var callbackInfo = obj as CancellationCallbackInfo; 
            callbackInfo.Callback(callbackInfo.StateForCallback);
        } 
    }

    /// <summary>
    /// A sparsely populated array.  Elements can be sparse and some null, but this allows for 
    /// lock-free additions and growth, and also for constant time removal (by nulling out).
    /// </summary>
    /// <typeparam name="T">The kind of elements contained within.</typeparam>
    internal class SparselyPopulatedArray<T> where T : class 
    {
        private readonly SparselyPopulatedArrayFragment<T> m_head; 
        private SparselyPopulatedArrayFragment<T> m_tail; 

        /// <summary> 
        /// Allocates a new array with the given initial size.
        /// </summary>
        /// <param name="initialSize">How many array slots to pre-allocate.</param>
        internal SparselyPopulatedArray(int initialSize) 
        {
            m_head = m_tail = new SparselyPopulatedArrayFragment<T>(initialSize); 
        } 

        /// <summary> 
        /// The head of the doubly linked list.
        /// </summary>
        internal SparselyPopulatedArrayFragment<T> Head
        { 
            get { return m_head; }
        } 
 
        /// <summary>
        /// The tail of the doubly linked list. 
        /// </summary>
        internal SparselyPopulatedArrayFragment<T> Tail
        {
            get { return m_tail; } 
        }
 
        /// <summary> 
        /// Adds an element in the first available slot, beginning the search from the tail-to-head.
        /// If no slots are available, the array is grown.  The method doesn't return until successful. 
        /// </summary>
        /// <param name="element">The element to add.</param>
        /// <returns>Information about where the add happened, to enable O(1) deregistration.</returns>
        internal SparselyPopulatedArrayAddInfo<T> Add(T element) 
        {
            while (true) 
            { 
                // Get the tail, and ensure it's up to date.
                SparselyPopulatedArrayFragment<T> tail = m_tail; 
                while (tail.m_next != null)
                    m_tail = (tail = tail.m_next);

                // Search for a free index, starting from the tail. 
                SparselyPopulatedArrayFragment<T> curr = tail;
                while (curr != null) 
                { 
                    const int RE_SEARCH_THRESHOLD = -10; // Every 10 skips, force a search.
                    if (curr.m_freeCount < 1) 
                        --curr.m_freeCount;

                    if (curr.m_freeCount > 0 || curr.m_freeCount < RE_SEARCH_THRESHOLD)
                    { 
                        int c = curr.Length;
 
                        // We'll compute a start offset based on how many free slots we think there 
                        // are.  This optimizes for ordinary the LIFO deregistration pattern, and is
                        // far from perfect due to the non-threadsafe ++ and -- of the free counter. 
                        int start = ((c - curr.m_freeCount) % c);
                        if (start < 0)
                        {
                            start = 0; 
                            curr.m_freeCount--; // Too many free elements; fix up.
                        } 

                        // Now walk the array until we find a free slot (or reach the end). 
                        for (int i = 0; i < c; i++)
                        {
                            // If the slot is null, try to CAS our element into it.
                            int tryIndex = (start + i) % c; 
 
                            if (curr.m_elements[tryIndex] == null && Interlocked.CompareExchange(ref curr.m_elements[tryIndex], element, null) == null) 
                            {
                                // We adjust the free count by --. Note: if this drops to 0, we will skip 
                                // the fragment on the next search iteration.  Searching threads will -- the
                                // count and force a search every so often, just in case fragmentation occurs.
                                int newFreeCount = curr.m_freeCount - 1;
                                curr.m_freeCount = newFreeCount > 0 ? newFreeCount : 0; 
                                return new SparselyPopulatedArrayAddInfo<T>(curr, tryIndex);
                            } 
                        } 
                    }
 
                    curr = curr.m_prev;
                }

                // If we got here, we need to add a new chunk to the tail and try again. 
                SparselyPopulatedArrayFragment<T> newTail = new SparselyPopulatedArrayFragment<T>(
                    tail.m_elements.Length == 4096 ? 4096 : tail.m_elements.Length * 2, tail); 
                if (Interlocked.CompareExchange(ref tail.m_next, newTail, null) == null) 
                {
                    m_tail = newTail; 
                }
            }
        }
    } 

    /// <summary> 
    /// A struct to hold a link to the exact spot in an array an element was inserted, enabling 
    /// constant time removal later on.
    /// </summary> 
    internal struct SparselyPopulatedArrayAddInfo<T> where T : class
    {
        private SparselyPopulatedArrayFragment<T> m_source;
        private int m_index; 

        internal SparselyPopulatedArrayAddInfo(SparselyPopulatedArrayFragment<T> source, int index) 
        { 
            m_source = source;
            m_index = index;
        }
 
        internal SparselyPopulatedArrayFragment<T> Source
        { 
            get { return m_source; } 
        }
 
        internal int Index
        {
            get { return m_index; }
        } 
    }
 
    /// <summary> 
    /// A fragment of a sparsely populated array, doubly linked.
    /// </summary> 
    /// <typeparam name="T">The kind of elements contained within.</typeparam>
    internal class SparselyPopulatedArrayFragment<T> where T : class
    {
        internal readonly T[] m_elements; // The contents, sparsely populated (with nulls). 
        internal int m_freeCount; // A hint of the number of free elements.
        internal SparselyPopulatedArrayFragment<T> m_next; // The next fragment in the chain. 
        internal SparselyPopulatedArrayFragment<T> m_prev; // The previous fragment in the chain. 

        internal SparselyPopulatedArrayFragment(int size) : this(size, null) 
        {
        }

        internal SparselyPopulatedArrayFragment(int size, SparselyPopulatedArrayFragment<T> prev) 
        {
            m_elements = new T[size]; 
            m_freeCount = size; 
            m_prev = prev;
        } 

        internal T this[int index]
        {
            get { return m_elements[index]; } 
        }
 
        internal int Length 
        {
            get { return m_elements.Length; } 
        }

        internal SparselyPopulatedArrayFragment<T> Next
        { 
            get { return m_next; }
        } 
 
        internal SparselyPopulatedArrayFragment<T> Prev
        { 
            get { return m_prev; }
        }

        // only removes the item at the specified index if it is still the expected one. 
        // Returns the prevailing value.
        // The remove occured successfully if the return value == expected element 
        // otherwise the remove did not occur. 
        internal T SafeAtomicRemove(int index, T expectedElement)
        { 
            T prevailingValue = Interlocked.CompareExchange(ref m_elements[index], null, expectedElement);
            if (prevailingValue != null)
                ++m_freeCount;
            return prevailingValue; 
        }
    } 
}