using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

public class Issue
{
    private string _title;
    private bool _closed;
    private List<string> _expectedLabels;
    private List<string> _expectedComments;
    private List<string> _unexpectedLabels;
    private List<string> _unexpectedComments;

    public Issue(string title = "")
    {
        _title = title;
        _closed = false;
        _expectedLabels = new List<string>();
        _expectedComments = new List<string>();
        _unexpectedLabels = new List<string>();
        _unexpectedComments = new List<string>();
    }

    public string Title
    {
        get { return _title; }
    }

    public void CreateComment(string text)
    {
        if (_expectedComments.Count == 0)
        {
            _unexpectedComments.Add(text);
        }
        else if (!Regex.IsMatch(text, _expectedComments[0]))
        {
            _unexpectedComments.Add(text);
        }
        else
        {
            _expectedComments.RemoveAt(0);
        }
    }

    public void Edit(string state = "opened", List<string> labels = null)
    {
        if (state == "closed")
        {
            _closed = true;
        }

        if (labels != null)
        {
            foreach (string label in labels)
            {
                if (_expectedLabels.Contains(label))
                {
                    _expectedLabels.Remove(label);
                }
                else
                {
                    _unexpectedLabels.Add(label);
                }
            }
        }
    }

    public void AddToLabels(string label)
    {
        if (_expectedLabels.Contains(label))
        {
            _expectedLabels.Remove(label);
        }
        else
        {
            _unexpectedLabels.Add(label);
        }
    }

    ####################### Testing functions #######################

    public void ExpectLabels(List<string> labels)
    {
        _expectedLabels = labels;
    }

    public void ExpectComments(List<string> regexList)
    {
        _expectedComments = regexList;
    }

    public (bool, string) ExpectationsFulfilled()
    {
        if (_expectedLabels.Count != 0)
        {
            return (false, $"Missing expected labels: {_expectedLabels}");
        }
        if (_expectedComments.Count != 0)
        {
            return (false, $"Missing expected comments: {_expectedComments}");
        }
        if (_unexpectedLabels.Count != 0)
        {
            return (false, $"Unexpected labels: {_unexpectedLabels}");
        }
        if (_unexpectedComments.Count != 0)
        {
            return (false, $"Unexpected comments: {_unexpectedComments}");
        }
        if (!_closed)
        {
            return (false, "Issue not closed");
        }

        return (true, null);
    }
}
