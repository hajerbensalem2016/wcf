// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Diagnostics.Contracts;
using System.IdentityModel.Policy;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Runtime;
using System.Security.Authentication;
using System.Security.Principal;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.ServiceModel.Diagnostics.Application;
using System.ServiceModel.Security;
using System.ServiceModel.Security.Tokens;
using System.Threading.Tasks;

namespace System.ServiceModel.Channels
{
    class WindowsStreamSecurityUpgradeProvider : StreamSecurityUpgradeProvider
    {
        bool _extractGroupsForWindowsAccounts;
        EndpointIdentity _identity;
        IdentityVerifier _identityVerifier;
        ProtectionLevel _protectionLevel;
        SecurityTokenManager _securityTokenManager;
        NetworkCredential _serverCredential;
        string _scheme;
        bool _isClient;
        Uri _listenUri;

        public WindowsStreamSecurityUpgradeProvider(WindowsStreamSecurityBindingElement bindingElement,
            BindingContext context, bool isClient)
            : base(context.Binding)
        {
            Contract.Assert(isClient, ".NET Core and .NET Native does not support server side");

            _extractGroupsForWindowsAccounts = TransportDefaults.ExtractGroupsForWindowsAccounts;
            _protectionLevel = bindingElement.ProtectionLevel;
            _scheme = context.Binding.Scheme;
            _isClient = isClient;
            _listenUri = TransportSecurityHelpers.GetListenUri(context.ListenUriBaseAddress, context.ListenUriRelativeAddress);

            SecurityCredentialsManager credentialProvider = context.BindingParameters.Find<SecurityCredentialsManager>();
            if (credentialProvider == null)
            {
                credentialProvider = ClientCredentials.CreateDefaultCredentials();
            }

            _securityTokenManager = credentialProvider.CreateSecurityTokenManager();
        }

        public string Scheme
        {
            get { return _scheme; }
        }

        internal bool ExtractGroupsForWindowsAccounts
        {
            get
            {
                return _extractGroupsForWindowsAccounts;
            }
        }

        public override EndpointIdentity Identity
        {
            get
            {
                // If the server credential is null, then we have not been opened yet and have no identity to expose.
                if (_serverCredential != null)
                {
                    if (_identity == null)
                    {
                        lock (ThisLock)
                        {
                            if (_identity == null)
                            {
                                _identity = SecurityUtils.CreateWindowsIdentity(_serverCredential);
                            }
                        }
                    }
                }
                return _identity;
            }
        }

        internal IdentityVerifier IdentityVerifier
        {
            get
            {
                return _identityVerifier;
            }
        }

        public ProtectionLevel ProtectionLevel
        {
            get
            {
                return _protectionLevel;
            }
        }

        NetworkCredential ServerCredential
        {
            get
            {
                return _serverCredential;
            }
        }

        public override StreamUpgradeAcceptor CreateUpgradeAcceptor()
        {
            ThrowIfDisposedOrNotOpen();
            return new WindowsStreamSecurityUpgradeAcceptor(this);
        }

        public override StreamUpgradeInitiator CreateUpgradeInitiator(EndpointAddress remoteAddress, Uri via)
        {
            ThrowIfDisposedOrNotOpen();
            return new WindowsStreamSecurityUpgradeInitiator(this, remoteAddress, via);
        }

        protected override void OnAbort()
        {
        }

        protected override void OnClose(TimeSpan timeout)
        {
        }

        protected override IAsyncResult OnBeginClose(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return new CompletedAsyncResult(callback, state);
        }

        protected override void OnEndClose(IAsyncResult result)
        {
            CompletedAsyncResult.End(result);
        }

        protected override void OnOpen(TimeSpan timeout)
        {
            if (!_isClient)
            {
                SecurityTokenRequirement sspiTokenRequirement = TransportSecurityHelpers.CreateSspiTokenRequirement(Scheme, _listenUri);
                _serverCredential =
                    TransportSecurityHelpers.GetSspiCredential(_securityTokenManager, sspiTokenRequirement, timeout,
                    out _extractGroupsForWindowsAccounts);
            }
        }

        protected override IAsyncResult OnBeginOpen(TimeSpan timeout, AsyncCallback callback, object state)
        {
            OnOpen(timeout);
            return new CompletedAsyncResult(callback, state);
        }

        protected override void OnEndOpen(IAsyncResult result)
        {
            CompletedAsyncResult.End(result);
        }

        protected override void OnOpened()
        {
            base.OnOpened();

            if (_identityVerifier == null)
            {
                _identityVerifier = IdentityVerifier.CreateDefault();
            }

            if (_serverCredential == null)
            {
                _serverCredential = CredentialCache.DefaultNetworkCredentials;
            }
        }

        private class WindowsStreamSecurityUpgradeAcceptor : StreamSecurityUpgradeAcceptorBase
        {
            private WindowsStreamSecurityUpgradeProvider _parent;
            private SecurityMessageProperty _clientSecurity;

            public WindowsStreamSecurityUpgradeAcceptor(WindowsStreamSecurityUpgradeProvider parent)
                : base(FramingUpgradeString.Negotiate)
            {
                _parent = parent;
                _clientSecurity = new SecurityMessageProperty();
            }

            protected override Stream OnAcceptUpgrade(Stream stream, out SecurityMessageProperty remoteSecurity)
            {
#if FEATURE_NETNATIVE // NegotiateStream
                throw ExceptionHelper.PlatformNotSupported("NegotiateStream is not supported on UWP yet"); 
#else 
                // wrap stream
                NegotiateStream negotiateStream = new NegotiateStream(stream);

                // authenticate
                try
                {
                    if (TD.WindowsStreamSecurityOnAcceptUpgradeIsEnabled())
                    {
                        TD.WindowsStreamSecurityOnAcceptUpgrade(EventTraceActivity);
                    }

                    negotiateStream.AuthenticateAsServer(_parent.ServerCredential, _parent.ProtectionLevel,
                        TokenImpersonationLevel.Identification);
                }
                catch (AuthenticationException exception)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new SecurityNegotiationException(exception.Message,
                        exception));
                }
                catch (IOException ioException)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new SecurityNegotiationException(
                        SR.Format(SR.NegotiationFailedIO, ioException.Message), ioException));
                }

                remoteSecurity = CreateClientSecurity(negotiateStream, _parent.ExtractGroupsForWindowsAccounts);
                return negotiateStream;
#endif // FEATURE_NETNATIVE
            }

            protected override IAsyncResult OnBeginAcceptUpgrade(Stream stream, AsyncCallback callback, object state)
            {
                throw ExceptionHelper.PlatformNotSupported(); 
            }

            protected override Stream OnEndAcceptUpgrade(IAsyncResult result,
                out SecurityMessageProperty remoteSecurity)
            {
                throw ExceptionHelper.PlatformNotSupported();
            }

#if !FEATURE_NETNATIVE // NegotiateStream
            SecurityMessageProperty CreateClientSecurity(NegotiateStream negotiateStream,
                bool extractGroupsForWindowsAccounts)
            {
                WindowsIdentity remoteIdentity = (WindowsIdentity)negotiateStream.RemoteIdentity;
                SecurityUtils.ValidateAnonymityConstraint(remoteIdentity, false);
                WindowsSecurityTokenAuthenticator authenticator = new WindowsSecurityTokenAuthenticator(extractGroupsForWindowsAccounts);

                // When NegotiateStream returns a WindowsIdentity the AuthenticationType is passed in the constructor to WindowsIdentity
                // by it's internal NegoState class.  If this changes, then the call to remoteIdentity.AuthenticationType could fail if the 
                // current process token doesn't have sufficient priviledges.  It is a first class exception, and caught by the CLR
                // null is returned.
                SecurityToken token = new WindowsSecurityToken(remoteIdentity, SecurityUniqueId.Create().Value, remoteIdentity.AuthenticationType);
                ReadOnlyCollection<IAuthorizationPolicy> authorizationPolicies = authenticator.ValidateToken(token);
                _clientSecurity = new SecurityMessageProperty();
                _clientSecurity.TransportToken = new SecurityTokenSpecification(token, authorizationPolicies);
                _clientSecurity.ServiceSecurityContext = new ServiceSecurityContext(authorizationPolicies);
                return _clientSecurity;
            }
#endif // !FEATURE_NETNATIVE

            public override SecurityMessageProperty GetRemoteSecurity()
            {
                if (_clientSecurity.TransportToken != null)
                {
                    return _clientSecurity;
                }
                return base.GetRemoteSecurity();
            }
        }

        class WindowsStreamSecurityUpgradeInitiator : StreamSecurityUpgradeInitiatorBase
        {
            private WindowsStreamSecurityUpgradeProvider _parent;
            private IdentityVerifier _identityVerifier;
            private NetworkCredential _credential;
            private TokenImpersonationLevel _impersonationLevel;
            private SspiSecurityTokenProvider _clientTokenProvider;
            private bool _allowNtlm;

            public WindowsStreamSecurityUpgradeInitiator(
                WindowsStreamSecurityUpgradeProvider parent, EndpointAddress remoteAddress, Uri via)
                : base(FramingUpgradeString.Negotiate, remoteAddress, via)
            {
                _parent = parent;
                _clientTokenProvider = TransportSecurityHelpers.GetSspiTokenProvider(
                    parent._securityTokenManager, remoteAddress, via, parent.Scheme, out _identityVerifier);
            }

            internal override async Task OpenAsync(TimeSpan timeout)
            {
                TimeoutHelper timeoutHelper = new TimeoutHelper(timeout);
                base.Open(timeoutHelper.RemainingTime());

                OutWrapper<TokenImpersonationLevel> impersonationLevelWrapper = new OutWrapper<TokenImpersonationLevel>();
                OutWrapper<bool> allowNtlmWrapper = new OutWrapper<bool>(); 
                
                SecurityUtils.OpenTokenProviderIfRequired(_clientTokenProvider, timeoutHelper.RemainingTime());
                _credential = await TransportSecurityHelpers.GetSspiCredentialAsync(
                    _clientTokenProvider,
                    impersonationLevelWrapper,
                    allowNtlmWrapper,
                    timeoutHelper.GetCancellationToken());

                _impersonationLevel = impersonationLevelWrapper.Value;
                _allowNtlm = allowNtlmWrapper; 

                return; 
            }

            internal override void Open(TimeSpan timeout)
            {
                OpenAsync(timeout).GetAwaiter();
            }
            
            internal override void Close(TimeSpan timeout)
            {
                TimeoutHelper timeoutHelper = new TimeoutHelper(timeout);
                base.Close(timeoutHelper.RemainingTime());
                SecurityUtils.CloseTokenProviderIfRequired(_clientTokenProvider, timeoutHelper.RemainingTime());
            }

#if !FEATURE_NETNATIVE // NegotiateStream
            static SecurityMessageProperty CreateServerSecurity(NegotiateStream negotiateStream)
            {
                GenericIdentity remoteIdentity = (GenericIdentity)negotiateStream.RemoteIdentity;
                string principalName = remoteIdentity.Name;
                if ((principalName != null) && (principalName.Length > 0))
                {
                    ReadOnlyCollection<IAuthorizationPolicy> authorizationPolicies = SecurityUtils.CreatePrincipalNameAuthorizationPolicies(principalName);
                    SecurityMessageProperty result = new SecurityMessageProperty();
                    result.TransportToken = new SecurityTokenSpecification(null, authorizationPolicies);
                    result.ServiceSecurityContext = new ServiceSecurityContext(authorizationPolicies);
                    return result;
                }
                else
                {
                    return null;
                }
            }
#endif // !FEATURE_NETNATIVE

            protected override Stream OnInitiateUpgrade(Stream stream, out SecurityMessageProperty remoteSecurity)
            {
                OutWrapper<SecurityMessageProperty> remoteSecurityOut = new OutWrapper<SecurityMessageProperty>(); 
                
                var retVal = OnInitiateUpgradeAsync(stream, remoteSecurityOut).GetAwaiter().GetResult();
                remoteSecurity = remoteSecurityOut.Value;

                return retVal;
            }


#if FEATURE_NETNATIVE // NegotiateStream
            protected override Task<Stream> OnInitiateUpgradeAsync(Stream stream, OutWrapper<SecurityMessageProperty> remoteSecurity)
            {
                throw ExceptionHelper.PlatformNotSupported("Windows Stream Security is not supported on UWP yet"); 
            }
#else
            protected override async Task<Stream> OnInitiateUpgradeAsync(Stream stream, OutWrapper<SecurityMessageProperty> remoteSecurity)
            {
                NegotiateStream negotiateStream;
                string targetName;
                EndpointIdentity identity;

                if (TD.WindowsStreamSecurityOnInitiateUpgradeIsEnabled())
                {
                    TD.WindowsStreamSecurityOnInitiateUpgrade();
                }

                // prepare
                InitiateUpgradePrepare(stream, out negotiateStream, out targetName, out identity);

                // authenticate
                try
                {
                    await negotiateStream.AuthenticateAsClientAsync(_credential, targetName, _parent.ProtectionLevel, _impersonationLevel);
                }
                catch (AuthenticationException exception)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new SecurityNegotiationException(exception.Message,
                        exception));
                }
                catch (IOException ioException)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new SecurityNegotiationException(
                        SR.Format(SR.NegotiationFailedIO, ioException.Message), ioException));
                }

                remoteSecurity.Value = CreateServerSecurity(negotiateStream);
                ValidateMutualAuth(identity, negotiateStream, remoteSecurity.Value, _allowNtlm);

                return negotiateStream;
            }
#endif // FEATURE_NETNATIVE 

#if !FEATURE_NETNATIVE // NegotiateStream
            void InitiateUpgradePrepare(
                Stream stream,
                out NegotiateStream negotiateStream,
                out string targetName,
                out EndpointIdentity identity)
            {
                negotiateStream = new NegotiateStream(stream);

                targetName = string.Empty;
                identity = null;

                if (_parent.IdentityVerifier.TryGetIdentity(RemoteAddress, Via, out identity))
                {
                    targetName = SecurityUtils.GetSpnFromIdentity(identity, RemoteAddress);
                }
                else
                {
                    targetName = SecurityUtils.GetSpnFromTarget(RemoteAddress);
                }
            }

            void ValidateMutualAuth(EndpointIdentity expectedIdentity, NegotiateStream negotiateStream,
                SecurityMessageProperty remoteSecurity, bool allowNtlm)
            {
                if (negotiateStream.IsMutuallyAuthenticated)
                {
                    if (expectedIdentity != null)
                    {
                        if (!_parent.IdentityVerifier.CheckAccess(expectedIdentity,
                            remoteSecurity.ServiceSecurityContext.AuthorizationContext))
                        {
                            string primaryIdentity = SecurityUtils.GetIdentityNamesFromContext(remoteSecurity.ServiceSecurityContext.AuthorizationContext);
                            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new SecurityNegotiationException(SR.Format(
                                SR.RemoteIdentityFailedVerification, primaryIdentity)));
                        }
                    }
                }
                else if (!allowNtlm)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new SecurityNegotiationException(SR.Format(SR.StreamMutualAuthNotSatisfied)));
                }
            }
#endif // FEATURE_NETNATIVE 
        }
    }
}
