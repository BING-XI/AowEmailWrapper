using System;
using System.Collections.Generic;
using System.Diagnostics;
using AowEmailWrapper.ConfigFramework;
using AowEmailWrapper.Games;
using AowEmailWrapper.Helpers;
using AowEmailWrapper.Pollers.MessageStore;
using MailKit.Net.Pop3;
using MimeKit;

namespace AowEmailWrapper.Pollers
{
    public class Pop3Poller : BasePoller
    {
        public Pop3Poller(
            string host,
            int port,
            SSLType sslType,
            string username,
            string password,
            int pollInterval,
            EmailSaveFolder saveFolder,
            AowGameManager gameManager)
            : base(
            host,
            port,
            sslType,
            username,
            password,
            pollInterval,
            saveFolder,
            gameManager)
        { }

        protected override void Poll()
        {
            bool emailDownloaded = false;
            Exception error = null;

            //A stopped poller begins no check, so the Wrapper can close once the one under way is done
            if (!TryPollBegin())
            {
                return;
            }

            try
            {
                using (Pop3Client pop3 = new Pop3Client())
                {
                    pop3.Timeout = NETWORK_TIMEOUT_MS;
                    pop3.Connect(_host, _port, MailHelper.ToSecureSocketOptions(_sslType));
                    MailHelper.Authenticate(pop3, _username, _password, OAuthProvider);

                    //POP3 addresses messages by index; UIDL gives the stable id for each index
                    IList<string> uidl = pop3.GetMessageUids();
                    List<string> serverUids = new List<string>(uidl);
                    Dictionary<string, int> uidIndex = new Dictionary<string, int>();
                    for (int i = 0; i < uidl.Count; i++)
                    {
                        if (!uidIndex.ContainsKey(uidl[i]))
                        {
                            uidIndex.Add(uidl[i], i);
                        }
                    }

                    MessageStoreCollection localMessageStore = MessageStoreManager.LoadLocalMessageStore(_username, _host);

                    if (localMessageStore != null)
                    {
                        MessageStoreManager.RemoveMessagesNoLongerOnServer(ref localMessageStore, serverUids);

                        List<string> uidsToCheck = MessageStoreManager.GetMessagesToCheck(localMessageStore, serverUids);

                        foreach (string uid in uidsToCheck)
                        {
                            int index;
                            if (!uidIndex.TryGetValue(uid, out index))
                            {
                                continue;
                            }

                            MimeMessage email = pop3.GetMessage(index);

                            if (HandleWrapperMessage(email))
                            {
                                //Wrapper chatter is dealt with and removed, never kept or recorded
                                pop3.DeleteMessage(index);
                                continue;
                            }

                            MessageStoreMessage theMessage = new MessageStoreMessage(uid);

                            bool recorded;
                            if (ProcessEmailAttachments(email, out recorded) > 0)
                            {
                                if (!recorded)
                                {
                                    //The activity log did not take the turn: the Wrapper is closing, or storing it
                                    //failed. Left out of the message store, so the next check fetches it again
                                    Trace.TraceWarning("The turn in message {0} was not recorded; left for the next check", uid);
                                    continue;
                                }

                                emailDownloaded = true;

                                //Only populate the extra data for game emails
                                theMessage.From = MailHelper.GetFromAddress(email);
                                theMessage.Subject = email.Subject;

                                if (email.Date != DateTimeOffset.MinValue)
                                {
                                    DateTime stamp = email.Date.LocalDateTime;
                                    theMessage.Date = stamp.ToString();
                                    theMessage.DateTicks = stamp.Ticks.ToString();
                                }
                                //In case the email doesn't come down with a good date
                                if (string.IsNullOrEmpty(theMessage.Date))
                                {
                                    DateTime stamp = DateTime.Now;
                                    theMessage.Date = stamp.ToString();
                                    theMessage.DateTicks = stamp.Ticks.ToString();
                                }

                                theMessage.FileName = MailHelper.GetAttachmentsString(email);
                            }

                            localMessageStore.Messages.Add(theMessage);
                        }
                    }
                    else
                    {
                        //New message store, add all currently on server
                        localMessageStore = new MessageStoreCollection(serverUids);
                    }

                    MessageStoreManager.SaveLocalMessageStore(_username, _host, localMessageStore);

                    localMessageStore.Dispose();
                    localMessageStore = null;

                    pop3.Disconnect(true);
                }
            }
            catch (Exception ex)
            {
                error = ex;
                Trace.TraceError(ex.ToString());
                Trace.Flush();
            }
            finally
            {
                PollEnd(emailDownloaded, error);
            }
        }
    }
}
